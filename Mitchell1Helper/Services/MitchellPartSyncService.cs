using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class MitchellPartSyncService(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    RecordHashCache hashCache,
    SyncFailureTracker failureTracker,
    ILogger<MitchellPartSyncService> logger)
    : SyncServiceBase(appSettings, odooRpcClient, syncState, hashCache, failureTracker)
{
    private DateTime _lastChangeDateUtc = syncState.LastProcessedPartChangeDateUtc ?? DateTime.MinValue;
    private long _lastPartId = syncState.LastProcessedPartId ?? 0;

    public async Task PollAndSyncAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(AppSettings.Sql.ConnectionString))
        {
            const string msg = "SQL connection string is empty. Update AppData/Mitchell1Helper/settings.json.";
            SyncState.MarkPartError(msg);
            logger.LogWarning(msg);
            return;
        }

        logger.LogInformation("Starting Part poll. Last: ChangeDate={Date:O}, Id={Id}",
            _lastChangeDateUtc, _lastPartId);

        try
        {
            var (cursor, processed, failed) = await PollFlatEntityAsync(
                entityName: "Part",
                sqlTable: Mitchell1Schema.PartsTable,
                sqlIdColumn: Mitchell1Schema.PartIdColumn,
                sqlIsDeletedColumn: Mitchell1Schema.IsDeletedColumn,
                odooModel: AppSettings.Odoo.PartModel,
                odooDomainField: "part_id",
                cacheKeyPrefix: "part",
                useSnakeCase: true,
                lastChangeDate: _lastChangeDateUtc,
                lastId: _lastPartId,
                buildValues: (reader, cols, id, _) => BuildValues(reader, cols, id),
                getDomainValue: id => id,
                logger: logger,
                cancellationToken: cancellationToken);

            if (cursor.HasAdvanced)
            {
                _lastChangeDateUtc = cursor.ChangeDate!.Value;
                _lastPartId = cursor.Id!.Value;
                SyncState.MarkPartSuccess(cursor.ChangeDate, cursor.Id, processed);
            }

            string? quarantined = QuarantineSummary("Part");
            if (failed > 0 || quarantined != null)
            {
                string msg = failed > 0
                    ? $"{failed} part record(s) failed to sync at ChangeDate={_lastChangeDateUtc:O}, Id={_lastPartId}. See log for details."
                    : "";
                if (quarantined != null) msg = string.IsNullOrEmpty(msg) ? quarantined : msg + " " + quarantined;
                SyncState.MarkPartError(msg);
                logger.LogWarning(msg);
            }

            logger.LogInformation("Part poll completed. Processed {Count} change(s), {Failed} failure(s)", processed, failed);
        }
        catch (Exception ex)
        {
            SyncState.MarkPartError(ex.Message);
            logger.LogError(ex, "Error polling Part changes");
            throw;
        }
    }

    private static Dictionary<string, object> BuildValues(SqlDataReader reader, HashSet<string> columns, long id)
    {
        var values = new Dictionary<string, object>
        {
            ["PartId"] = id,
            ["Active"] = true
        };

        string? description = GetString(reader, columns, "Description");
        if (!string.IsNullOrWhiteSpace(description)) values["Description"] = description;
        string? comment = GetString(reader, columns, "Comment");
        if (!string.IsNullOrWhiteSpace(comment)) values["Comment"] = comment;
        string? size = GetString(reader, columns, "Size");
        if (!string.IsNullOrWhiteSpace(size)) values["Size"] = size;
        string? metadata = GetString(reader, columns, "Metadata");
        if (!string.IsNullOrWhiteSpace(metadata)) values["Metadata"] = metadata;

        if (TryGetDecimal(reader, columns, "Cost", out decimal? cost)) values["Cost"] = (double)cost!.Value;
        if (TryGetDecimal(reader, columns, "Price", out decimal? price)) values["Price"] = (double)price!.Value;
        if (TryGetDecimal(reader, columns, "ListPrice", out decimal? listPrice)) values["ListPrice"] = (double)listPrice!.Value;
        if (TryGetDecimal(reader, columns, "CoreCost", out decimal? coreCost)) values["CoreCost"] = (double)coreCost!.Value;
        if (TryGetDecimal(reader, columns, "CoreListPrice", out decimal? coreListPrice)) values["CoreListPrice"] = (double)coreListPrice!.Value;

        if (TryGetBool(reader, columns, "TaxExempt", out bool taxExempt)) values["TaxExempt"] = taxExempt;
        if (TryGetBool(reader, columns, "IsTire", out bool isTire)) values["IsTire"] = isTire;
        if (TryGetBool(reader, columns, "IsCore", out bool isCore)) values["IsCore"] = isCore;
        if (TryGetBool(reader, columns, "UserEnteredPrice", out bool userEnteredPrice)) values["UserEnteredPrice"] = userEnteredPrice;
        if (TryGetBool(reader, columns, "IsMPLGItem", out bool isMPLGItem)) values["IsMPLGItem"] = isMPLGItem;
        if (TryGetBool(reader, columns, "IsChangedMPLGItem", out bool isChangedMPLGItem)) values["IsChangedMPLGItem"] = isChangedMPLGItem;

        if (TryGetInt(reader, columns, "CategoryId", out int? categoryId)) values["CategoryId"] = categoryId!.Value;
        if (TryGetInt(reader, columns, "AccountClassId", out int? accountClassId)) values["AccountClassId"] = accountClassId!.Value;
        if (TryGetInt(reader, columns, "ManufacturerId", out int? manufacturerId)) values["ManufacturerId"] = manufacturerId!.Value;
        if (TryGetInt(reader, columns, "KitId", out int? kitId)) values["KitId"] = kitId!.Value;
        if (TryGetInt(reader, columns, "PartFeeId", out int? partFeeId)) values["PartFeeId"] = partFeeId!.Value;
        if (TryGetInt(reader, columns, "RecordVersion", out int? recordVersion)) values["RecordVersion"] = recordVersion!.Value;

        return values;
    }
}
