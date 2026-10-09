using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class MitchellRepairOrderSyncService(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    RecordHashCache hashCache,
    SyncFailureTracker failureTracker,
    ILogger<MitchellRepairOrderSyncService> logger)
    : SyncServiceBase(appSettings, odooRpcClient, syncState, hashCache, failureTracker)
{
    private DateTime _lastChangeDateUtc = syncState.LastProcessedRepairOrderChangeDateUtc ?? DateTime.MinValue;
    private long _lastRepairOrderId = syncState.LastProcessedRepairOrderId ?? 0;

    public async Task PollAndSyncAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(AppSettings.Sql.ConnectionString))
        {
            const string msg = "SQL connection string is empty. Update AppData/Mitchell1Helper/settings.json.";
            SyncState.MarkRepairOrderError(msg);
            logger.LogWarning(msg);
            return;
        }

        int totalProcessed = 0;

        // Drain all pending batches before moving on to line items.
        while (true)
        {
            int processed = 0;
            int failed = 0;
            var cursor = new CursorTracker();
            try
            {
                logger.LogInformation("Starting RepairOrder poll. Last: ChangeDate={Date:O}, Id={Id}",
                    _lastChangeDateUtc, _lastRepairOrderId);

                string cs = EnsureTrustServerCertificate(AppSettings.Sql.ConnectionString);
                await using var conn = new SqlConnection(cs);
                await conn.OpenAsync(cancellationToken);

                string table = QuoteIdentifier(Mitchell1Schema.RepairOrderTable);
                string idCol = QuoteIdentifier(Mitchell1Schema.RepairOrderIdColumn);
                string changeDateCol = QuoteIdentifier(Mitchell1Schema.LastChangeDateColumn);

                await using var cmd = new SqlCommand(BuildCursorQuery(table, idCol, changeDateCol), conn);
                cmd.Parameters.AddWithValue("@BatchSize", AppSettings.Sync.BatchSize);
                cmd.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = _lastChangeDateUtc;
                cmd.Parameters.AddWithValue("@LastId", _lastRepairOrderId);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                var (columns, changeDateOrdinal) = ReadColumnSchema(reader);

                while (await reader.ReadAsync(cancellationToken))
                {
                    long repairOrderId = 0;
                    DateTime? changeDate = null;
                    try
                    {
                        repairOrderId = Convert.ToInt64(reader[Mitchell1Schema.RepairOrderIdColumn]);
                        changeDate = ParseChangeDate(reader, changeDateOrdinal, repairOrderId, "RepairOrder", logger);

                        var values = ConvertKeysToSnakeCase(BuildRepairOrderValues(reader, columns, repairOrderId));
                        await UpsertInOdooAsync(
                            AppSettings.Odoo.RepairOrderModel, "repair_order_id", repairOrderId,
                            values, $"repair_order:{repairOrderId}", cancellationToken);

                        MarkRowSucceeded("RepairOrder", repairOrderId);
                        processed++;
                        cursor.Advance(changeDate.Value, repairOrderId);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        HandleRowFailure("RepairOrder", repairOrderId, changeDate, cursor, ex, logger);
                    }
                }

                if (cursor.HasAdvanced)
                {
                    _lastChangeDateUtc = cursor.ChangeDate!.Value;
                    _lastRepairOrderId = cursor.Id!.Value;
                    SyncState.MarkRepairOrderSuccess(cursor.ChangeDate, cursor.Id, processed);
                }
            }
            catch (Exception ex)
            {
                SyncState.MarkRepairOrderError(ex.Message);
                logger.LogError(ex, "Error polling RepairOrder changes");
                throw;
            }

            string? roQuarantined = QuarantineSummary("RepairOrder");
            if (failed > 0 || roQuarantined != null)
            {
                string msg = failed > 0
                    ? $"{failed} repair order(s) failed to sync at ChangeDate={_lastChangeDateUtc:O}, Id={_lastRepairOrderId}. See log for details."
                    : "";
                if (roQuarantined != null) msg = string.IsNullOrEmpty(msg) ? roQuarantined : msg + " " + roQuarantined;
                SyncState.MarkRepairOrderError(msg);
            }
            else
            {
                // A clean poll clears a stale error. Without this, an entity that has had
                // no changes keeps reporting a problem that was resolved days ago.
                SyncState.MarkRepairOrderError(null);
            }

            totalProcessed += processed;
            logger.LogInformation("RepairOrder batch: {Batch} processed, {Failed} failed, {Total} total",
                processed, failed, totalProcessed);
            // A full batch means there is probably more waiting, so keep draining. But only if the
            // cursor actually moved — re-running the identical query would just re-read the same
            // rows and burn the retry budget of a blocked row in milliseconds instead of across polls.
            if (processed + failed < AppSettings.Sync.BatchSize || !cursor.HasAdvanced) break;
        }

        await PollAndSyncLineItemsAsync(cancellationToken);
    }

    private async Task PollAndSyncLineItemsAsync(CancellationToken cancellationToken)
    {
        int totalProcessed = 0;

        while (true)
        {
            int processed = 0;
            int failed = 0;
            var cursor = new CursorTracker();

            try
            {
                DateTime lastChangeDate = SyncState.LastProcessedRepairOrderLineItemChangeDateUtc ?? DateTime.MinValue;
                long lastLineItemId = SyncState.LastProcessedRepairOrderLineItemId ?? 0;

                logger.LogInformation("Starting LineItem poll. Last: ChangeDate={Date:O}, Id={Id}",
                    lastChangeDate, lastLineItemId);

                string cs = EnsureTrustServerCertificate(AppSettings.Sql.ConnectionString);
                await using var conn = new SqlConnection(cs);
                await conn.OpenAsync(cancellationToken);

                string liTable = QuoteIdentifier(Mitchell1Schema.LineItemTable);
                string seqTable = QuoteIdentifier(Mitchell1Schema.RepairOrderLineItemSequenceTable);
                string liIdCol = QuoteIdentifier(Mitchell1Schema.LineItemIdColumn);
                string roIdCol = QuoteIdentifier(Mitchell1Schema.RepairOrderIdColumn);
                string changeDateCol = QuoteIdentifier(Mitchell1Schema.LastChangeDateColumn);

                string query = $"""
                    SELECT TOP (@BatchSize)
                        li.*,
                        seq.{roIdCol},
                        CONVERT(NVARCHAR(50), li.{changeDateCol}, 127) AS LastChangeDate_String
                    FROM {liTable} li
                    INNER JOIN {seqTable} seq ON li.{liIdCol} = seq.{liIdCol}
                    WHERE (li.{changeDateCol} > @LastChangeDate)
                       OR (li.{changeDateCol} = @LastChangeDate AND li.{liIdCol} > @LastId)
                    ORDER BY li.{changeDateCol} ASC, li.{liIdCol} ASC;
                    """;

                await using var cmd = new SqlCommand(query, conn);
                cmd.Parameters.AddWithValue("@BatchSize", AppSettings.Sync.BatchSize);
                cmd.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = lastChangeDate;
                cmd.Parameters.AddWithValue("@LastId", lastLineItemId);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                var (columns, changeDateOrdinal) = ReadColumnSchema(reader);

                while (await reader.ReadAsync(cancellationToken))
                {
                    long lineItemId = 0;
                    DateTime? changeDate = null;
                    try
                    {
                        lineItemId = Convert.ToInt64(reader[Mitchell1Schema.LineItemIdColumn]);
                        long repairOrderId = Convert.ToInt64(reader[Mitchell1Schema.RepairOrderIdColumn]);
                        changeDate = ParseChangeDate(reader, changeDateOrdinal, lineItemId, "LineItem", logger);

                        var values = ConvertKeysToSnakeCase(BuildLineItemValues(reader, columns, lineItemId, repairOrderId));
                        await UpsertInOdooAsync(
                            AppSettings.Odoo.RepairOrderLineItemModel, "line_item_id", lineItemId,
                            values, $"line_item:{lineItemId}", cancellationToken);

                        MarkRowSucceeded("LineItem", lineItemId);
                        processed++;
                        cursor.Advance(changeDate.Value, lineItemId);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        HandleRowFailure("LineItem", lineItemId, changeDate, cursor, ex, logger);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error polling LineItem changes");
                break;
            }

            if (cursor.HasAdvanced)
                SyncState.MarkRepairOrderLineItemSuccess(cursor.ChangeDate, cursor.Id, processed);

            string? liQuarantined = QuarantineSummary("LineItem");
            if (failed > 0 || liQuarantined != null)
            {
                string msg = failed > 0
                    ? $"{failed} line item(s) failed to sync. See log for details."
                    : "";
                if (liQuarantined != null) msg = string.IsNullOrEmpty(msg) ? liQuarantined : msg + " " + liQuarantined;
                SyncState.MarkRepairOrderLineItemError(msg);
            }
            else
            {
                // A clean poll clears a stale error. Without this, an entity that has had
                // no changes keeps reporting a problem that was resolved days ago.
                SyncState.MarkRepairOrderLineItemError(null);
            }

            totalProcessed += processed;
            logger.LogInformation("LineItem batch: {Batch} processed, {Failed} failed, {Total} total",
                processed, failed, totalProcessed);
            // See the RepairOrder drain loop: only keep going while the cursor is actually moving.
            if (processed + failed < AppSettings.Sync.BatchSize || !cursor.HasAdvanced) break;
        }
    }

    private static Dictionary<string, object> BuildRepairOrderValues(SqlDataReader reader, HashSet<string> columns, long repairOrderId)
    {
        var values = new Dictionary<string, object> { ["RepairOrderId"] = repairOrderId };

        if (TryGetInt(reader, columns, "RepairOrderPhaseId", out int? repairOrderPhaseId)) values["RepairOrderPhaseId"] = repairOrderPhaseId;
        if (TryGetInt(reader, columns, "CustId", out int? custId)) values["CustId"] = custId;
        if (TryGetInt(reader, columns, "VehicleId", out int? vehicleId)) values["VehicleId"] = vehicleId;
        if (TryGetInt(reader, columns, "CategoryId", out int? categoryId)) values["CategoryId"] = categoryId;
        if (TryGetInt(reader, columns, "ReasonForVisitId", out int? reasonForVisitId)) values["ReasonForVisitId"] = reasonForVisitId;
        if (TryGetInt(reader, columns, "OdometerIn", out int? odometerIn)) values["OdometerIn"] = odometerIn;
        if (TryGetInt(reader, columns, "OdometerOut", out int? odometerOut)) values["OdometerOut"] = odometerOut;
        if (TryGetInt(reader, columns, "PartDiscountDescriptionId", out int? partDiscountDescriptionId)) values["PartDiscountDescriptionId"] = partDiscountDescriptionId;
        if (TryGetInt(reader, columns, "LaborRateDescriptionId", out int? laborRateDescriptionId)) values["LaborRateDescriptionId"] = laborRateDescriptionId;
        if (TryGetInt(reader, columns, "RecordVersion", out int? recordVersion)) values["RecordVersion"] = recordVersion;
        if (TryGetLong(reader, columns, "AppointmentRequestUid", out long? appointmentRequestUid)) values["AppointmentRequestUid"] = appointmentRequestUid;

        if (TryGetString(reader, columns, "Location", out string? location)) values["Location"] = location;
        if (TryGetString(reader, columns, "StatusDescription", out string? statusDescription)) values["StatusDescription"] = statusDescription;
        if (TryGetString(reader, columns, "Hat", out string? hat)) values["Hat"] = hat;
        if (TryGetString(reader, columns, "ReferenceNumber", out string? referenceNumber)) values["ReferenceNumber"] = referenceNumber;
        if (TryGetString(reader, columns, "Notes", out string? notes)) values["Notes"] = notes;
        if (TryGetString(reader, columns, "Observations", out string? observations)) values["Observations"] = observations;
        if (TryGetString(reader, columns, "AuthorizationStatus", out string? authorizationStatus)) values["AuthorizationStatus"] = authorizationStatus;
        if (TryGetString(reader, columns, "AuthorizationSignatureUrl", out string? authorizationSignatureUrl)) values["AuthorizationSignatureUrl"] = authorizationSignatureUrl;
        if (TryGetString(reader, columns, "WalkaroundSignatureUrl", out string? walkaroundSignatureUrl)) values["WalkaroundSignatureUrl"] = walkaroundSignatureUrl;

        if (TryGetDecimal(reader, columns, "ScheduledHours", out decimal? scheduledHours)) values["ScheduledHours"] = (double)scheduledHours!.Value;
        if (TryGetDecimal(reader, columns, "TaxAmtHazMat", out decimal? taxAmtHazMat)) values["TaxAmtHazMat"] = (double)taxAmtHazMat!.Value;
        if (TryGetDecimal(reader, columns, "TaxAmtShopSupplies", out decimal? taxAmtShopSupplies)) values["TaxAmtShopSupplies"] = (double)taxAmtShopSupplies!.Value;
        if (TryGetDecimal(reader, columns, "OrderTotal", out decimal? orderTotal)) values["OrderTotal"] = (double)orderTotal!.Value;
        if (TryGetDecimal(reader, columns, "HazWasteAmt", out decimal? hazWasteAmt)) values["HazWasteAmt"] = (double)hazWasteAmt!.Value;
        if (TryGetDecimal(reader, columns, "ShopSuppliesAmt", out decimal? shopSuppliesAmt)) values["ShopSuppliesAmt"] = (double)shopSuppliesAmt!.Value;
        if (TryGetDecimal(reader, columns, "TotalTaxAmt", out decimal? totalTaxAmt)) values["TotalTaxAmt"] = (double)totalTaxAmt!.Value;
        if (TryGetDecimal(reader, columns, "LaborSale", out decimal? laborSale)) values["LaborSale"] = (double)laborSale!.Value;
        if (TryGetDecimal(reader, columns, "PartsSale", out decimal? partsSale)) values["PartsSale"] = (double)partsSale!.Value;
        if (TryGetDecimal(reader, columns, "DiscountAmt", out decimal? discountAmt)) values["DiscountAmt"] = (double)discountAmt!.Value;
        if (TryGetDecimal(reader, columns, "BalanceDueAdjustment", out decimal? balanceDueAdjustment)) values["BalanceDueAdjustment"] = (double)balanceDueAdjustment!.Value;
        if (TryGetDecimal(reader, columns, "MarginPct", out decimal? marginPct)) values["MarginPct"] = (double)marginPct!.Value;
        if (TryGetDecimal(reader, columns, "TireFeeSale", out decimal? tireFeeSale)) values["TireFeeSale"] = (double)tireFeeSale!.Value;
        if (TryGetDecimal(reader, columns, "EngineHoursIn", out decimal? engineHoursIn)) values["EngineHoursIn"] = (double)engineHoursIn!.Value;
        if (TryGetDecimal(reader, columns, "EngineHoursOut", out decimal? engineHoursOut)) values["EngineHoursOut"] = (double)engineHoursOut!.Value;

        if (TryGetDateTime(reader, columns, "ScheduleDate", out DateTime? scheduleDate)) values["ScheduleDate"] = scheduleDate!.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (TryGetDateTime(reader, columns, "PromiseDate", out DateTime? promiseDate)) values["PromiseDate"] = promiseDate!.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (TryGetDateTime(reader, columns, "TimeIn", out DateTime? timeIn)) values["TimeIn"] = timeIn!.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (TryGetDateTime(reader, columns, "TimeOut", out DateTime? timeOut)) values["TimeOut"] = timeOut!.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (TryGetDateTime(reader, columns, "DatePosted", out DateTime? datePosted)) values["DatePosted"] = datePosted!.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (TryGetDateTime(reader, columns, "PrintedDate", out DateTime? printedDate)) values["PrintedDate"] = printedDate!.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (TryGetDateTime(reader, columns, "RateVersionDate", out DateTime? rateVersionDate)) values["RateVersionDate"] = rateVersionDate!.Value.ToString("yyyy-MM-dd HH:mm:ss");

        if (TryGetBool(reader, columns, "RoPrinted", out bool roPrinted)) values["RoPrinted"] = roPrinted;
        if (TryGetBool(reader, columns, "InvoicePrinted", out bool invoicePrinted)) values["InvoicePrinted"] = invoicePrinted;
        if (TryGetBool(reader, columns, "CalculateHazWasteCost", out bool calculateHazWasteCost)) values["CalculateHazWasteCost"] = calculateHazWasteCost;
        if (TryGetBool(reader, columns, "CalculateShopSuppliesCost", out bool calculateShopSuppliesCost)) values["CalculateShopSuppliesCost"] = calculateShopSuppliesCost;
        if (TryGetBool(reader, columns, "TaxExempt", out bool taxExempt)) values["TaxExempt"] = taxExempt;
        if (TryGetBool(reader, columns, "FromQuickEst", out bool fromQuickEst)) values["FromQuickEst"] = fromQuickEst;
        if (TryGetBool(reader, columns, "CreatedAsEstimate", out bool createdAsEstimate)) values["CreatedAsEstimate"] = createdAsEstimate;

        return values;
    }

    private static Dictionary<string, object> BuildLineItemValues(SqlDataReader reader, HashSet<string> columns, long lineItemId, long repairOrderId)
    {
        var values = new Dictionary<string, object>
        {
            ["LineItemId"] = lineItemId,
            ["RepairOrderId"] = repairOrderId
        };

        if (TryGetInt(reader, columns, "AcctClassId", out int? acctClassId)) values["AcctClassId"] = acctClassId;
        if (TryGetInt(reader, columns, "CategoryId", out int? categoryId)) values["CategoryId"] = categoryId;
        if (TryGetInt(reader, columns, "OrderRevisionId", out int? orderRevisionId)) values["OrderRevisionId"] = orderRevisionId;
        if (TryGetInt(reader, columns, "CannedJobId", out int? cannedJobId)) values["CannedJobId"] = cannedJobId;
        if (TryGetInt(reader, columns, "ParentLineItemId", out int? parentLineItemId)) values["ParentLineItemId"] = parentLineItemId;
        if (TryGetInt(reader, columns, "RecordVersion", out int? recordVersion)) values["RecordVersion"] = recordVersion;

        if (TryGetString(reader, columns, "Description", out string? description)) values["Description"] = description;
        if (TryGetString(reader, columns, "Metadata", out string? metadata)) values["Metadata"] = metadata;

        if (TryGetDecimal(reader, columns, "Cost", out decimal? cost)) values["Cost"] = (double)cost!.Value;
        if (TryGetDecimal(reader, columns, "Sale", out decimal? sale)) values["Sale"] = (double)sale!.Value;
        if (TryGetDecimal(reader, columns, "LaborSale", out decimal? laborSale)) values["LaborSale"] = (double)laborSale!.Value;
        if (TryGetDecimal(reader, columns, "PartSale", out decimal? partSale)) values["PartSale"] = (double)partSale!.Value;
        if (TryGetDecimal(reader, columns, "PartOnlySale", out decimal? partOnlySale)) values["PartOnlySale"] = (double)partOnlySale!.Value;
        if (TryGetDecimal(reader, columns, "LaborOnlySale", out decimal? laborOnlySale)) values["LaborOnlySale"] = (double)laborOnlySale!.Value;
        if (TryGetDecimal(reader, columns, "SubletSale", out decimal? subletSale)) values["SubletSale"] = (double)subletSale!.Value;
        if (TryGetDecimal(reader, columns, "PackageSale", out decimal? packageSale)) values["PackageSale"] = (double)packageSale!.Value;

        if (TryGetBool(reader, columns, "TaxExempt", out bool taxExempt)) values["TaxExempt"] = taxExempt;
        if (TryGetBool(reader, columns, "NoCommission", out bool noCommission)) values["NoCommission"] = noCommission;
        if (TryGetBool(reader, columns, "FixedCommission", out bool fixedCommission)) values["FixedCommission"] = fixedCommission;
        if (TryGetBool(reader, columns, "TireFee", out bool tireFee)) values["TireFee"] = tireFee;

        return values;
    }
}
