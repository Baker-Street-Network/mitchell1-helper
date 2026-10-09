using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class MitchellPOSyncService(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    RecordHashCache hashCache,
    SyncFailureTracker failureTracker,
    ILogger<MitchellPOSyncService> logger)
    : SyncServiceBase(appSettings, odooRpcClient, syncState, hashCache, failureTracker)
{
    private DateTime _lastChangeDateUtc = syncState.LastProcessedPOChangeDateUtc ?? DateTime.MinValue;
    private long _lastPOMasterId = syncState.LastProcessedPOMasterId ?? 0;

    public async Task PollAndSyncAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(AppSettings.Sql.ConnectionString))
        {
            const string msg = "SQL connection string is empty. Update AppData/Mitchell1Helper/settings.json.";
            SyncState.MarkPOError(msg);
            logger.LogWarning(msg);
            return;
        }

        logger.LogInformation("Starting PO poll. Last: ChangeDate={Date:O}, POMasterId={Id}",
            _lastChangeDateUtc, _lastPOMasterId);

        int processedHeaders = 0;
        int processedDetails = 0;
        int failedHeaders = 0;
        int failedDetails = 0;
        var cursor = new CursorTracker();

        try
        {
            string cs = EnsureTrustServerCertificate(AppSettings.Sql.ConnectionString);
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(cancellationToken);

            // ── PO Headers ────────────────────────────────────────────────────

            string masterTable = QuoteIdentifier(Mitchell1Schema.POMasterTable);
            string masterIdCol = QuoteIdentifier(Mitchell1Schema.POMasterIdColumn);
            string changeDateCol = QuoteIdentifier(Mitchell1Schema.LastChangeDateColumn);

            await using var headerCmd = new SqlCommand(BuildCursorQuery(masterTable, masterIdCol, changeDateCol), conn);
            headerCmd.Parameters.AddWithValue("@BatchSize", AppSettings.Sync.BatchSize);
            headerCmd.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = _lastChangeDateUtc;
            headerCmd.Parameters.AddWithValue("@LastId", _lastPOMasterId);

            await using var headerReader = await headerCmd.ExecuteReaderAsync(cancellationToken);
            var (headerColumns, headerChangeDateOrdinal) = ReadColumnSchema(headerReader);

            while (await headerReader.ReadAsync(cancellationToken))
            {
                long poMasterId = 0;
                DateTime? changeDate = null;
                try
                {
                    poMasterId = Convert.ToInt64(headerReader[Mitchell1Schema.POMasterIdColumn]);
                    changeDate = ParseChangeDate(headerReader, headerChangeDateOrdinal, poMasterId, "PO Header", logger);

                    var values = ConvertKeysToSnakeCase(BuildPOHeaderValues(headerReader, headerColumns));
                    await UpsertInOdooAsync(
                        AppSettings.Odoo.POHeaderModel, "po_master_id", poMasterId,
                        values, $"po_header:{poMasterId}", cancellationToken);

                    MarkRowSucceeded("PO Header", poMasterId);
                    processedHeaders++;
                    cursor.Advance(changeDate.Value, poMasterId);
                }
                catch (Exception ex)
                {
                    failedHeaders++;
                    HandleRowFailure("PO Header", poMasterId, changeDate, cursor, ex, logger);
                }
            }

            await headerReader.CloseAsync();

            // ── PO Details ────────────────────────────────────────────────────

            (processedDetails, failedDetails) = await SyncPODetailsAsync(conn, cancellationToken);

            if (cursor.HasAdvanced)
            {
                _lastChangeDateUtc = cursor.ChangeDate!.Value;
                _lastPOMasterId = cursor.Id!.Value;
                SyncState.MarkPOSuccess(cursor.ChangeDate, cursor.Id, processedHeaders, processedDetails);
            }

            string? poQuarantined = QuarantineSummary("PO Header", "PO Detail");
            int failed = failedHeaders + failedDetails;
            if (failed > 0 || poQuarantined != null)
            {
                string msg = failed > 0
                    ? $"{failed} purchase order record(s) failed to sync at ChangeDate={_lastChangeDateUtc:O}, Id={_lastPOMasterId}. See log for details."
                    : "";
                if (poQuarantined != null) msg = string.IsNullOrEmpty(msg) ? poQuarantined : msg + " " + poQuarantined;
                SyncState.MarkPOError(msg);
                logger.LogWarning(msg);
            }

            logger.LogInformation("PO poll completed. Headers: {Headers} ({FailedHeaders} failed), Details: {Details} ({FailedDetails} failed)",
                processedHeaders, failedHeaders, processedDetails, failedDetails);
        }
        catch (Exception ex)
        {
            SyncState.MarkPOError(ex.Message);
            logger.LogError(ex, "Error polling PO changes");
            throw;
        }
    }

    private async Task<(int processed, int failed)> SyncPODetailsAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        int processed = 0;
        int failed = 0;
        var cursor = new CursorTracker();

        string detailTable = QuoteIdentifier(Mitchell1Schema.PODetailTable);
        string detailIdCol = QuoteIdentifier(Mitchell1Schema.PODetailIdColumn);
        string changeDateCol = QuoteIdentifier(Mitchell1Schema.LastChangeDateColumn);

        // Details use a simpler > filter (no tie-breaking by id) because they have their own independent cursor.
        string query = $"""
            SELECT TOP (@BatchSize) *,
                   CONVERT(NVARCHAR(50), {changeDateCol}, 127) AS LastChangeDate_String
            FROM {detailTable}
            WHERE {changeDateCol} > @LastChangeDate
            ORDER BY {changeDateCol} ASC, {detailIdCol} ASC;
            """;

        await using var cmd = new SqlCommand(query, conn);
        cmd.Parameters.AddWithValue("@BatchSize", AppSettings.Sync.BatchSize * 10);
        cmd.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value =
            SyncState.LastProcessedPODetailChangeDateUtc ?? DateTime.MinValue;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var (columns, changeDateOrdinal) = ReadColumnSchema(reader);

        while (await reader.ReadAsync(cancellationToken))
        {
            long poDetailId = 0;
            DateTime? changeDate = null;
            try
            {
                poDetailId = Convert.ToInt64(reader[Mitchell1Schema.PODetailIdColumn]);
                changeDate = ParseChangeDate(reader, changeDateOrdinal, poDetailId, "PO Detail", logger);

                var values = ConvertKeysToSnakeCase(BuildPODetailValues(reader, columns));
                await UpsertInOdooAsync(
                    AppSettings.Odoo.PODetailModel, "po_detail_id", poDetailId,
                    values, $"po_detail:{poDetailId}", cancellationToken);

                MarkRowSucceeded("PO Detail", poDetailId);
                processed++;
                cursor.Advance(changeDate.Value, poDetailId);
            }
            catch (Exception ex)
            {
                failed++;
                HandleRowFailure("PO Detail", poDetailId, changeDate, cursor, ex, logger);
            }
        }

        if (cursor.HasAdvanced)
            SyncState.MarkPODetailSuccess(cursor.ChangeDate, cursor.Id);

        return (processed, failed);
    }

    private static Dictionary<string, object> BuildPOHeaderValues(SqlDataReader reader, HashSet<string> columns)
    {
        var values = new Dictionary<string, object>();

        AddValue(values, reader, columns, "POMasterId", required: true);
        AddValue(values, reader, columns, "PODate", required: true);
        AddValue(values, reader, columns, "RequiredDate", required: true);
        AddValue(values, reader, columns, "POShipViaId", required: true);
        AddValue(values, reader, columns, "IsClosed", required: true);
        AddValue(values, reader, columns, "POStateId", required: true);
        AddValue(values, reader, columns, "RecordVersion", required: false, defaultValue: 1);
        AddValue(values, reader, columns, "LastChangeDate", required: true);

        AddValue(values, reader, columns, "WriterId", required: false);
        AddValue(values, reader, columns, "VendorId", required: false);
        AddValue(values, reader, columns, "CommentInternal", required: false);
        AddValue(values, reader, columns, "CommentReport", required: false);
        AddValue(values, reader, columns, "TaxTotal", required: false);
        AddValue(values, reader, columns, "PartsTotal", required: false);
        AddValue(values, reader, columns, "PartsReceived", required: false);
        AddValue(values, reader, columns, "BackOrder", required: false);
        AddValue(values, reader, columns, "Phone", required: false);
        AddValue(values, reader, columns, "PhoneExt", required: false);
        AddValue(values, reader, columns, "Taxable", required: false);
        AddValue(values, reader, columns, "TaxReceived", required: false);
        AddValue(values, reader, columns, "ClosedDate", required: false);
        AddValue(values, reader, columns, "ConfirmNum", required: false);
        AddValue(values, reader, columns, "TrackingNumber", required: false);

        return values;
    }

    private static Dictionary<string, object> BuildPODetailValues(SqlDataReader reader, HashSet<string> columns)
    {
        var values = new Dictionary<string, object>();

        AddValue(values, reader, columns, "PODetailId", required: true);
        AddValue(values, reader, columns, "POMasterId", required: true);
        AddValue(values, reader, columns, "InventoryPart", required: true);
        AddValue(values, reader, columns, "LocationId", required: false, defaultValue: "");
        AddValue(values, reader, columns, "IsTire", required: false, defaultValue: false);
        AddValue(values, reader, columns, "RecordVersion", required: false, defaultValue: 1);
        AddValue(values, reader, columns, "LastChangeDate", required: true);

        AddValue(values, reader, columns, "Sequence", required: false);
        AddValue(values, reader, columns, "PartId", required: false);
        AddValue(values, reader, columns, "PartNumber", required: false);
        AddValue(values, reader, columns, "Description", required: false);
        AddValue(values, reader, columns, "Quantity", required: false);
        AddValue(values, reader, columns, "QuantityReceived", required: false);
        AddValue(values, reader, columns, "Cost", required: false);
        AddValue(values, reader, columns, "CoreCost", required: false);
        AddValue(values, reader, columns, "QuantityBackOrder", required: false);
        AddValue(values, reader, columns, "QuantityCancelled", required: false);
        AddValue(values, reader, columns, "Notes", required: false);
        AddValue(values, reader, columns, "QuantityAvailConf", required: false);
        AddValue(values, reader, columns, "MfgId", required: false);
        AddValue(values, reader, columns, "AltPartNo", required: false);
        AddValue(values, reader, columns, "AltMfgId", required: false);
        AddValue(values, reader, columns, "RepairOrderId", required: false);
        AddValue(values, reader, columns, "SaveDescription", required: false);
        AddValue(values, reader, columns, "WDLineCode", required: false);
        AddValue(values, reader, columns, "PartType", required: false);
        AddValue(values, reader, columns, "LocationName", required: false);
        AddValue(values, reader, columns, "ListPrice", required: false);
        AddValue(values, reader, columns, "CatalogVendorId", required: false);
        AddValue(values, reader, columns, "Metadata", required: false);
        AddValue(values, reader, columns, "AddedFromSupplier", required: false);
        AddValue(values, reader, columns, "PurchasedFromVendor", required: false);
        AddValue(values, reader, columns, "PurchasedFromSupplier", required: false);
        AddValue(values, reader, columns, "ShippingDescription", required: false);
        AddValue(values, reader, columns, "ShippingCost", required: false);

        return values;
    }
}
