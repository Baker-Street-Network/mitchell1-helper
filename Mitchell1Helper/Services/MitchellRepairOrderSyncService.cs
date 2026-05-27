using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class MitchellRepairOrderSyncService(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    ILogger<MitchellRepairOrderSyncService> logger)
{
    private static readonly Regex IdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private DateTime _lastChangeDateUtc = syncState.LastProcessedRepairOrderChangeDateUtc ?? DateTime.MinValue;
    private long _lastRepairOrderId = syncState.LastProcessedRepairOrderId ?? 0;

    private static string EnsureTrustServerCertificate(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            TrustServerCertificate = true
        };
        return builder.ConnectionString;
    }

    public async Task PollAndSyncAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appSettings.Sql.ConnectionString))
        {
            const string message = "SQL connection string is empty. Update AppData/Mitchell1Helper/settings.json.";
            syncState.MarkRepairOrderError(message);
            logger.LogWarning(message);
            return;
        }

        logger.LogInformation("Starting repair order poll. Last processed: ChangeDate={LastChangeDate:O}, RepairOrderId={LastRepairOrderId}",
            _lastChangeDateUtc, _lastRepairOrderId);

        int processed = 0;
        DateTime? newestChangeDate = null;
        long? newestRepairOrderId = null;

        try
        {
            string connectionString = EnsureTrustServerCertificate(appSettings.Sql.ConnectionString);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            string table = QuoteIdentifier(Mitchell1Schema.RepairOrderTable);
            string idColumn = QuoteIdentifier(Mitchell1Schema.RepairOrderIdColumn);
            string changeDateColumn = QuoteIdentifier(Mitchell1Schema.LastChangeDateColumn);

            // Select all columns plus a string version of LastChangeDate to avoid SqlDateTime overflow
            string query = $@"
SELECT TOP (@BatchSize) *, 
       CONVERT(NVARCHAR(50), {changeDateColumn}, 127) AS LastChangeDate_String
FROM {table}
WHERE ({changeDateColumn} > @LastChangeDate)
   OR ({changeDateColumn} = @LastChangeDate AND {idColumn} > @LastRepairOrderId)
ORDER BY {changeDateColumn} ASC, {idColumn} ASC;";

            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@BatchSize", appSettings.Sync.BatchSize);
            command.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = _lastChangeDateUtc;
            command.Parameters.AddWithValue("@LastRepairOrderId", _lastRepairOrderId);

            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int changeDateStringOrdinal = -1;
            foreach (var column in reader.GetColumnSchema())
            {
                if (!string.IsNullOrWhiteSpace(column.ColumnName))
                {
                    columns.Add(column.ColumnName);
                    if (column.ColumnName.Equals("LastChangeDate_String", StringComparison.OrdinalIgnoreCase))
                    {
                        changeDateStringOrdinal = column.ColumnOrdinal ?? -1;
                    }
                }
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                try
                {
                    long repairOrderId = reader.GetInt32(reader.GetOrdinal(Mitchell1Schema.RepairOrderIdColumn));

                    DateTime changeDate;
                    try
                    {
                        if (changeDateStringOrdinal >= 0 && !reader.IsDBNull(changeDateStringOrdinal))
                        {
                            string dateString = reader.GetString(changeDateStringOrdinal);
                            if (DateTime.TryParse(dateString, out changeDate))
                            {
                                changeDate = changeDate.Kind == DateTimeKind.Utc ? changeDate : changeDate.ToUniversalTime();
                            }
                            else
                            {
                                logger.LogWarning(
                                    "Could not parse LastChangeDate string '{DateString}' for repair order {RepairOrderId}. Using current UTC time.",
                                    dateString, repairOrderId);
                                changeDate = DateTime.UtcNow;
                            }

                            if (!Mitchell1Schema.IsValidSqlDateTime(changeDate))
                            {
                                logger.LogWarning(
                                    "Invalid date {Date} (from string: '{DateString}') for repair order {RepairOrderId}. Clamping to valid range.",
                                    changeDate, dateString, repairOrderId);
                                changeDate = Mitchell1Schema.ClampToValidRange(changeDate);
                            }
                        }
                        else
                        {
                            logger.LogWarning(
                                "LastChangeDate_String column missing or null for repair order {RepairOrderId}. Using current UTC time.",
                                repairOrderId);
                            changeDate = DateTime.UtcNow;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Failed to read LastChangeDate for repair order {RepairOrderId}. Using current UTC time instead.",
                            repairOrderId);
                        changeDate = DateTime.UtcNow;
                    }

                    Dictionary<string, object> values = BuildRepairOrderValues(reader, columns, repairOrderId);
                    await UpsertRepairOrderInOdooAsync(appSettings.Odoo.RepairOrderModel, repairOrderId, values, cancellationToken);

                    processed++;
                    newestChangeDate = changeDate;
                    newestRepairOrderId = repairOrderId;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to process repair order {RepairOrderId}. Skipping this record and continuing.",
                        reader.GetInt32(reader.GetOrdinal(Mitchell1Schema.RepairOrderIdColumn)));
                }
            }

            if (newestChangeDate.HasValue && newestRepairOrderId.HasValue)
            {
                _lastChangeDateUtc = newestChangeDate.Value;
                _lastRepairOrderId = newestRepairOrderId.Value;
                syncState.MarkRepairOrderSuccess(newestChangeDate, newestRepairOrderId, processed);
            }

            logger.LogInformation("Repair order polling completed. Processed {Count} repair order change(s)", processed);

            // Now sync line items
            await PollAndSyncLineItemsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            syncState.MarkRepairOrderError(ex.Message);
            logger.LogError(ex, "Error polling Mitchell1 Repair Order changes");
            throw;
        }
    }

    private async Task PollAndSyncLineItemsAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting repair order line item poll. Last processed: ChangeDate={LastChangeDate:O}, LineItemId={LastLineItemId}",
            syncState.LastProcessedRepairOrderLineItemChangeDateUtc ?? DateTime.MinValue,
            syncState.LastProcessedRepairOrderLineItemId ?? 0);

        int processed = 0;
        DateTime? newestChangeDate = null;
        long? newestLineItemId = null;

        try
        {
            string connectionString = EnsureTrustServerCertificate(appSettings.Sql.ConnectionString);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            string lineItemTable = QuoteIdentifier(Mitchell1Schema.LineItemTable);
            string sequenceTable = QuoteIdentifier(Mitchell1Schema.RepairOrderLineItemSequenceTable);
            string lineItemIdColumn = QuoteIdentifier(Mitchell1Schema.LineItemIdColumn);
            string repairOrderIdColumn = QuoteIdentifier(Mitchell1Schema.RepairOrderIdColumn);
            string changeDateColumn = QuoteIdentifier(Mitchell1Schema.LastChangeDateColumn);

            DateTime lastChangeDate = syncState.LastProcessedRepairOrderLineItemChangeDateUtc ?? DateTime.MinValue;
            long lastLineItemId = syncState.LastProcessedRepairOrderLineItemId ?? 0;

            // Join LineItem with RepairOrderLineItemSequence to get RepairOrderId correlation
            string query = $@"
SELECT TOP (@BatchSize) 
    li.*, 
    seq.{repairOrderIdColumn},
    CONVERT(NVARCHAR(50), li.{changeDateColumn}, 127) AS LastChangeDate_String
FROM {lineItemTable} li
INNER JOIN {sequenceTable} seq ON li.{lineItemIdColumn} = seq.{lineItemIdColumn}
WHERE (li.{changeDateColumn} > @LastChangeDate)
   OR (li.{changeDateColumn} = @LastChangeDate AND li.{lineItemIdColumn} > @LastLineItemId)
ORDER BY li.{changeDateColumn} ASC, li.{lineItemIdColumn} ASC;";

            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@BatchSize", appSettings.Sync.BatchSize);
            command.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = lastChangeDate;
            command.Parameters.AddWithValue("@LastLineItemId", lastLineItemId);

            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int changeDateStringOrdinal = -1;
            foreach (var column in reader.GetColumnSchema())
            {
                if (!string.IsNullOrWhiteSpace(column.ColumnName))
                {
                    columns.Add(column.ColumnName);
                    if (column.ColumnName.Equals("LastChangeDate_String", StringComparison.OrdinalIgnoreCase))
                    {
                        changeDateStringOrdinal = column.ColumnOrdinal ?? -1;
                    }
                }
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                try
                {
                    long lineItemId = reader.GetInt32(reader.GetOrdinal(Mitchell1Schema.LineItemIdColumn));
                    long repairOrderId = reader.GetInt32(reader.GetOrdinal(Mitchell1Schema.RepairOrderIdColumn));

                    DateTime changeDate;
                    try
                    {
                        if (changeDateStringOrdinal >= 0 && !reader.IsDBNull(changeDateStringOrdinal))
                        {
                            string dateString = reader.GetString(changeDateStringOrdinal);
                            if (DateTime.TryParse(dateString, out changeDate))
                            {
                                changeDate = changeDate.Kind == DateTimeKind.Utc ? changeDate : changeDate.ToUniversalTime();
                            }
                            else
                            {
                                changeDate = DateTime.UtcNow;
                            }

                            if (!Mitchell1Schema.IsValidSqlDateTime(changeDate))
                            {
                                changeDate = Mitchell1Schema.ClampToValidRange(changeDate);
                            }
                        }
                        else
                        {
                            changeDate = DateTime.UtcNow;
                        }
                    }
                    catch
                    {
                        changeDate = DateTime.UtcNow;
                    }

                    Dictionary<string, object> values = BuildLineItemValues(reader, columns, lineItemId, repairOrderId);
                    await UpsertLineItemInOdooAsync(appSettings.Odoo.RepairOrderLineItemModel, lineItemId, values, cancellationToken);

                    processed++;
                    newestChangeDate = changeDate;
                    newestLineItemId = lineItemId;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to process line item {LineItemId}. Skipping this record and continuing.",
                        reader.GetInt32(reader.GetOrdinal(Mitchell1Schema.LineItemIdColumn)));
                }
            }

            if (newestChangeDate.HasValue && newestLineItemId.HasValue)
            {
                syncState.MarkRepairOrderLineItemSuccess(newestChangeDate, newestLineItemId, processed);
            }

            logger.LogInformation("Repair order line item polling completed. Processed {Count} line item change(s)", processed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error polling Mitchell1 Repair Order Line Items");
        }
    }

    private async Task UpsertLineItemInOdooAsync(
        string model,
        long lineItemId,
        Dictionary<string, object> values,
        CancellationToken cancellationToken)
    {
        var snakeCaseValues = ConvertKeysToSnakeCase(values);

        var existingRecord = await FindLineItemByIdAsync(model, lineItemId, cancellationToken);
        if (existingRecord.HasValue)
        {
            int existingId = existingRecord.Value.id;
            var existingValues = existingRecord.Value.values;

            bool hasChanges = false;
            foreach (var (key, newValue) in snakeCaseValues)
            {
                if (key == "line_item_id") continue;

                if (!existingValues.TryGetProperty(key, out var existingValueElement))
                {
                    hasChanges = true;
                    break;
                }

                string? newValueStr = newValue?.ToString();
                string? existingValueStr = existingValueElement.ValueKind == System.Text.Json.JsonValueKind.False || existingValueElement.ValueKind == System.Text.Json.JsonValueKind.True
                    ? existingValueElement.GetBoolean().ToString()
                    : existingValueElement.ValueKind == System.Text.Json.JsonValueKind.String
                        ? existingValueElement.GetString()
                        : existingValueElement.ToString();

                if (newValueStr != existingValueStr)
                {
                    hasChanges = true;
                    break;
                }
            }

            if (hasChanges)
            {
                await odooRpcClient.ExecuteKwAsync(
                    model,
                    "write",
                    new object[] { new[] { existingId }, snakeCaseValues },
                    null,
                    cancellationToken);
                logger.LogInformation("Updated line item {LineItemId} in Odoo staging table", lineItemId);
            }
        }
        else
        {
            var result = await odooRpcClient.ExecuteKwAsync(
                model,
                "create",
                new object[] { snakeCaseValues },
                null,
                cancellationToken);
            int newId = result.GetInt32();
            logger.LogInformation("Created line item {LineItemId} in Odoo staging table", lineItemId);
        }
    }

    private async Task<(int id, System.Text.Json.JsonElement values)?> FindLineItemByIdAsync(
        string model,
        long lineItemId,
        CancellationToken cancellationToken)
    {
        object[] domain = { new object[] { "line_item_id", "=", lineItemId } };
        var kwargs = new Dictionary<string, object>
        {
            ["fields"] = new[] { "id", "line_item_id", "repair_order_id", "description" },
            ["limit"] = 1
        };

        var result = await odooRpcClient.ExecuteKwAsync(
            model,
            "search_read",
            new object[] { domain },
            kwargs,
            cancellationToken);

        if (result.ValueKind != System.Text.Json.JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            return null;
        }

        var record = result[0];
        int id = record.GetProperty("id").GetInt32();
        return (id, record);
    }

    private static Dictionary<string, object> BuildLineItemValues(
        SqlDataReader reader,
        HashSet<string> columns,
        long lineItemId,
        long repairOrderId)
    {
        var values = new Dictionary<string, object>
        {
            ["LineItemId"] = lineItemId,
            ["RepairOrderId"] = repairOrderId
        };

        // Integer fields
        if (TryGetInt(reader, columns, "AcctClassId", out int? acctClassId)) values["AcctClassId"] = acctClassId;
        if (TryGetInt(reader, columns, "CategoryId", out int? categoryId)) values["CategoryId"] = categoryId;
        if (TryGetInt(reader, columns, "OrderRevisionId", out int? orderRevisionId)) values["OrderRevisionId"] = orderRevisionId;
        if (TryGetInt(reader, columns, "CannedJobId", out int? cannedJobId)) values["CannedJobId"] = cannedJobId;
        if (TryGetInt(reader, columns, "ParentLineItemId", out int? parentLineItemId)) values["ParentLineItemId"] = parentLineItemId;
        if (TryGetInt(reader, columns, "RecordVersion", out int? recordVersion)) values["RecordVersion"] = recordVersion;

        // String fields
        if (TryGetString(reader, columns, "Description", out string? description)) values["Description"] = description;
        if (TryGetString(reader, columns, "Metadata", out string? metadata)) values["Metadata"] = metadata;

        // Decimal/Money fields
        if (TryGetDecimal(reader, columns, "Cost", out decimal? cost)) values["Cost"] = (double)cost;
        if (TryGetDecimal(reader, columns, "Sale", out decimal? sale)) values["Sale"] = (double)sale;
        if (TryGetDecimal(reader, columns, "LaborSale", out decimal? laborSale)) values["LaborSale"] = (double)laborSale;
        if (TryGetDecimal(reader, columns, "PartSale", out decimal? partSale)) values["PartSale"] = (double)partSale;
        if (TryGetDecimal(reader, columns, "PartOnlySale", out decimal? partOnlySale)) values["PartOnlySale"] = (double)partOnlySale;
        if (TryGetDecimal(reader, columns, "LaborOnlySale", out decimal? laborOnlySale)) values["LaborOnlySale"] = (double)laborOnlySale;
        if (TryGetDecimal(reader, columns, "SubletSale", out decimal? subletSale)) values["SubletSale"] = (double)subletSale;
        if (TryGetDecimal(reader, columns, "PackageSale", out decimal? packageSale)) values["PackageSale"] = (double)packageSale;

        // Boolean fields
        if (TryGetBool(reader, columns, "TaxExempt", out bool taxExempt)) values["TaxExempt"] = taxExempt;
        if (TryGetBool(reader, columns, "NoCommission", out bool noCommission)) values["NoCommission"] = noCommission;
        if (TryGetBool(reader, columns, "FixedCommission", out bool fixedCommission)) values["FixedCommission"] = fixedCommission;
        if (TryGetBool(reader, columns, "TireFee", out bool tireFee)) values["TireFee"] = tireFee;

        return values;
    }

    private async Task UpsertRepairOrderInOdooAsync(
        string model,
        long repairOrderId,
        Dictionary<string, object> values,
        CancellationToken cancellationToken)
    {
        // Convert keys to snake_case for Odoo
        var snakeCaseValues = ConvertKeysToSnakeCase(values);

        var existingRecord = await FindRepairOrderByIdAsync(model, repairOrderId, cancellationToken);
        if (existingRecord.HasValue)
        {
            int existingId = existingRecord.Value.id;
            var existingValues = existingRecord.Value.values;

            // Only write if values have actually changed
            bool hasChanges = false;
            foreach (var (key, newValue) in snakeCaseValues)
            {
                if (key == "repair_order_id") continue; // Skip ID field (it's the lookup key)

                if (!existingValues.TryGetProperty(key, out var existingValueElement))
                {
                    hasChanges = true;
                    break;
                }

                string? newValueStr = newValue?.ToString();
                string? existingValueStr = existingValueElement.ValueKind == System.Text.Json.JsonValueKind.False || existingValueElement.ValueKind == System.Text.Json.JsonValueKind.True
                    ? existingValueElement.GetBoolean().ToString()
                    : existingValueElement.ValueKind == System.Text.Json.JsonValueKind.String
                        ? existingValueElement.GetString()
                        : existingValueElement.ToString();

                if (newValueStr != existingValueStr)
                {
                    hasChanges = true;
                    break;
                }
            }

            if (hasChanges)
            {
                await odooRpcClient.ExecuteKwAsync(
                    model,
                    "write",
                    new object[] { new[] { existingId }, snakeCaseValues },
                    null,
                    cancellationToken);
                logger.LogInformation("Updated repair order {RepairOrderId} in Odoo staging table", repairOrderId);
            }
            else
            {
                logger.LogDebug("Repair order {RepairOrderId} unchanged, skipping write", repairOrderId);
            }
        }
        else
        {
            var result = await odooRpcClient.ExecuteKwAsync(
                model,
                "create",
                new object[] { snakeCaseValues },
                null,
                cancellationToken);
            int newId = result.GetInt32();
            logger.LogInformation("Created repair order {RepairOrderId} in Odoo staging table", repairOrderId);
        }
    }

    private async Task<(int id, System.Text.Json.JsonElement values)?> FindRepairOrderByIdAsync(
        string model,
        long repairOrderId,
        CancellationToken cancellationToken)
    {
        object[] domain = { new object[] { "repair_order_id", "=", repairOrderId } };
        var kwargs = new Dictionary<string, object>
        {
            ["fields"] = new[] { "id", "repair_order_id", "cust_id", "vehicle_id" },
            ["limit"] = 1
        };

        var result = await odooRpcClient.ExecuteKwAsync(
            model,
            "search_read",
            new object[] { domain },
            kwargs,
            cancellationToken);

        if (result.ValueKind != System.Text.Json.JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            return null;
        }

        var record = result[0];
        int id = record.GetProperty("id").GetInt32();
        return (id, record);
    }

    private static Dictionary<string, object> BuildRepairOrderValues(
        SqlDataReader reader,
        HashSet<string> columns,
        long repairOrderId)
    {
        var values = new Dictionary<string, object>
        {
            ["RepairOrderId"] = repairOrderId
        };

        // Integer fields
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

        // BigInt fields
        if (TryGetLong(reader, columns, "AppointmentRequestUid", out long? appointmentRequestUid)) values["AppointmentRequestUid"] = appointmentRequestUid;

        // String fields
        if (TryGetString(reader, columns, "Location", out string? location)) values["Location"] = location;
        if (TryGetString(reader, columns, "StatusDescription", out string? statusDescription)) values["StatusDescription"] = statusDescription;
        if (TryGetString(reader, columns, "Hat", out string? hat)) values["Hat"] = hat;
        if (TryGetString(reader, columns, "ReferenceNumber", out string? referenceNumber)) values["ReferenceNumber"] = referenceNumber;
        if (TryGetString(reader, columns, "Notes", out string? notes)) values["Notes"] = notes;
        if (TryGetString(reader, columns, "Observations", out string? observations)) values["Observations"] = observations;
        if (TryGetString(reader, columns, "AuthorizationStatus", out string? authorizationStatus)) values["AuthorizationStatus"] = authorizationStatus;
        if (TryGetString(reader, columns, "AuthorizationSignatureUrl", out string? authorizationSignatureUrl)) values["AuthorizationSignatureUrl"] = authorizationSignatureUrl;
        if (TryGetString(reader, columns, "WalkaroundSignatureUrl", out string? walkaroundSignatureUrl)) values["WalkaroundSignatureUrl"] = walkaroundSignatureUrl;

        // Decimal/Money fields
        if (TryGetDecimal(reader, columns, "ScheduledHours", out decimal? scheduledHours)) values["ScheduledHours"] = (double)scheduledHours;
        if (TryGetDecimal(reader, columns, "TaxAmtHazMat", out decimal? taxAmtHazMat)) values["TaxAmtHazMat"] = (double)taxAmtHazMat;
        if (TryGetDecimal(reader, columns, "TaxAmtShopSupplies", out decimal? taxAmtShopSupplies)) values["TaxAmtShopSupplies"] = (double)taxAmtShopSupplies;
        if (TryGetDecimal(reader, columns, "OrderTotal", out decimal? orderTotal)) values["OrderTotal"] = (double)orderTotal;
        if (TryGetDecimal(reader, columns, "HazWasteAmt", out decimal? hazWasteAmt)) values["HazWasteAmt"] = (double)hazWasteAmt;
        if (TryGetDecimal(reader, columns, "ShopSuppliesAmt", out decimal? shopSuppliesAmt)) values["ShopSuppliesAmt"] = (double)shopSuppliesAmt;
        if (TryGetDecimal(reader, columns, "TotalTaxAmt", out decimal? totalTaxAmt)) values["TotalTaxAmt"] = (double)totalTaxAmt;
        if (TryGetDecimal(reader, columns, "LaborSale", out decimal? laborSale)) values["LaborSale"] = (double)laborSale;
        if (TryGetDecimal(reader, columns, "PartsSale", out decimal? partsSale)) values["PartsSale"] = (double)partsSale;
        if (TryGetDecimal(reader, columns, "DiscountAmt", out decimal? discountAmt)) values["DiscountAmt"] = (double)discountAmt;
        if (TryGetDecimal(reader, columns, "BalanceDueAdjustment", out decimal? balanceDueAdjustment)) values["BalanceDueAdjustment"] = (double)balanceDueAdjustment;
        if (TryGetDecimal(reader, columns, "MarginPct", out decimal? marginPct)) values["MarginPct"] = (double)marginPct;
        if (TryGetDecimal(reader, columns, "TireFeeSale", out decimal? tireFeeSale)) values["TireFeeSale"] = (double)tireFeeSale;
        if (TryGetDecimal(reader, columns, "EngineHoursIn", out decimal? engineHoursIn)) values["EngineHoursIn"] = (double)engineHoursIn;
        if (TryGetDecimal(reader, columns, "EngineHoursOut", out decimal? engineHoursOut)) values["EngineHoursOut"] = (double)engineHoursOut;

        // DateTime fields
        if (TryGetDateTime(reader, columns, "ScheduleDate", out DateTime? scheduleDate)) values["ScheduleDate"] = scheduleDate.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (TryGetDateTime(reader, columns, "PromiseDate", out DateTime? promiseDate)) values["PromiseDate"] = promiseDate.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (TryGetDateTime(reader, columns, "TimeIn", out DateTime? timeIn)) values["TimeIn"] = timeIn.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (TryGetDateTime(reader, columns, "TimeOut", out DateTime? timeOut)) values["TimeOut"] = timeOut.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (TryGetDateTime(reader, columns, "DatePosted", out DateTime? datePosted)) values["DatePosted"] = datePosted.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (TryGetDateTime(reader, columns, "PrintedDate", out DateTime? printedDate)) values["PrintedDate"] = printedDate.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (TryGetDateTime(reader, columns, "RateVersionDate", out DateTime? rateVersionDate)) values["RateVersionDate"] = rateVersionDate.Value.ToString("yyyy-MM-dd HH:mm:ss");

        // Boolean fields
        if (TryGetBool(reader, columns, "RoPrinted", out bool roPrinted)) values["RoPrinted"] = roPrinted;
        if (TryGetBool(reader, columns, "InvoicePrinted", out bool invoicePrinted)) values["InvoicePrinted"] = invoicePrinted;
        if (TryGetBool(reader, columns, "CalculateHazWasteCost", out bool calculateHazWasteCost)) values["CalculateHazWasteCost"] = calculateHazWasteCost;
        if (TryGetBool(reader, columns, "CalculateShopSuppliesCost", out bool calculateShopSuppliesCost)) values["CalculateShopSuppliesCost"] = calculateShopSuppliesCost;
        if (TryGetBool(reader, columns, "TaxExempt", out bool taxExempt)) values["TaxExempt"] = taxExempt;
        if (TryGetBool(reader, columns, "FromQuickEst", out bool fromQuickEst)) values["FromQuickEst"] = fromQuickEst;
        if (TryGetBool(reader, columns, "CreatedAsEstimate", out bool createdAsEstimate)) values["CreatedAsEstimate"] = createdAsEstimate;

        return values;
    }

    private static Dictionary<string, object> ConvertKeysToSnakeCase(Dictionary<string, object> values)
    {
        var result = new Dictionary<string, object>();
        foreach (var (key, value) in values)
        {
            result[ToSnakeCase(key)] = value;
        }
        return result;
    }

    private static string ToSnakeCase(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        // Handle common acronyms that should stay together
        var acronyms = new[] { "ID", "PO", "RO", "UTC", "URL" };
        foreach (var acronym in acronyms)
        {
            if (input.Contains(acronym))
            {
                input = input.Replace(acronym, char.ToUpper(acronym[0]) + acronym.Substring(1).ToLower());
            }
        }

        // Insert underscores before uppercase letters (except first char)
        var chars = new List<char>();
        for (int i = 0; i < input.Length; i++)
        {
            if (i > 0 && char.IsUpper(input[i]) && (i == input.Length - 1 || !char.IsUpper(input[i + 1]) || (i > 0 && !char.IsUpper(input[i - 1]))))
            {
                chars.Add('_');
            }
            chars.Add(char.ToLower(input[i]));
        }

        return new string(chars.ToArray());
    }

    private static bool TryGetString(SqlDataReader reader, HashSet<string> columns, string column, out string? value)
    {
        value = null;
        if (!columns.Contains(column))
        {
            return false;
        }

        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        value = reader.GetString(ordinal);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetInt(SqlDataReader reader, HashSet<string> columns, string column, out int? value)
    {
        value = null;
        if (!columns.Contains(column))
        {
            return false;
        }

        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        value = reader.GetInt32(ordinal);
        return true;
    }

    private static bool TryGetLong(SqlDataReader reader, HashSet<string> columns, string column, out long? value)
    {
        value = null;
        if (!columns.Contains(column))
        {
            return false;
        }

        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        value = reader.GetInt64(ordinal);
        return true;
    }

    private static bool TryGetDecimal(SqlDataReader reader, HashSet<string> columns, string column, out decimal? value)
    {
        value = null;
        if (!columns.Contains(column))
        {
            return false;
        }

        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        value = reader.GetDecimal(ordinal);
        return true;
    }

    private static bool TryGetDateTime(SqlDataReader reader, HashSet<string> columns, string column, out DateTime? value)
    {
        value = null;
        if (!columns.Contains(column))
        {
            return false;
        }

        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        value = reader.GetDateTime(ordinal);
        return true;
    }

    private static bool TryGetBool(SqlDataReader reader, HashSet<string> columns, string column, out bool value)
    {
        value = false;
        if (!columns.Contains(column))
        {
            return false;
        }

        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        object val = reader.GetValue(ordinal);
        if (val is bool boolValue)
        {
            value = boolValue;
            return true;
        }

        if (val is int intValue)
        {
            value = intValue != 0;
            return true;
        }

        return false;
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier cannot be null or whitespace.", nameof(identifier));
        }

        if (identifier.Contains('[') || identifier.Contains(']'))
        {
            return identifier;
        }

        string[] parts = identifier.Split('.');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (!IdentifierRegex.IsMatch(part))
            {
                parts[i] = $"[{part.Replace("]", "]]")}]";
            }
        }

        return string.Join(".", parts);
    }
}
