using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class MitchellAccountSyncService(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    RecordHashCache hashCache,
    SyncFailureTracker failureTracker,
    ILogger<MitchellAccountSyncService> logger)
    : SyncServiceBase(appSettings, odooRpcClient, syncState, hashCache, failureTracker)
{
    private const string GLAccountQuery = """
        SELECT g.[GLAccountId], g.[AccountClassId], g.[GLCode], g.[Description], g.[EntryTypeId],
               g.[LastChangeDate], et.[EntryTypeDescription]
        FROM [SM].[GLAccounts] g
        LEFT JOIN [SM].[GLEntryTypes] et ON et.[EntryTypeId] = g.[EntryTypeId]
        ORDER BY g.[GLAccountId] ASC;
        """;

    public async Task PollAndSyncAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(AppSettings.Sql.ConnectionString))
        {
            const string msg = "SQL connection string is empty. Update AppData/Mitchell1Helper/settings.json.";
            SyncState.MarkGLAccountError(msg);
            logger.LogWarning(msg);
            return;
        }

        logger.LogInformation("Starting GL Account poll (full snapshot)");

        int processed = 0;
        int failed = 0;
        // Ids present in Mitchell1 this poll, whether or not they synced. ArchiveMissingAsync
        // compares against this, so a record that merely failed to sync is not mistaken for one
        // that was deleted and archived in Odoo.
        var presentIds = new List<long>();

        try
        {
            string cs = EnsureTrustServerCertificate(AppSettings.Sql.ConnectionString);
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = new SqlCommand(GLAccountQuery, conn);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var (columns, _) = ReadColumnSchema(reader);

            var pending = new List<(long Id, Dictionary<string, object> Values)>();
            while (await reader.ReadAsync(cancellationToken))
            {
                long id = 0;
                try
                {
                    id = Convert.ToInt64(reader[Mitchell1Schema.GLAccountIdColumn]);
                    pending.Add((id, BuildValues(reader, columns, id)));
                    presentIds.Add(id);
                }
                catch (Exception ex)
                {
                    failed++;
                    logger.LogError(ex, "Failed to read GL Account {Id}. Skipping.", id);
                }
            }

            await reader.CloseAsync();

            // Unlike the cursor-based polls, this one re-reads every GL account every time, so
            // existence is resolved for the whole snapshot in one call instead of one per account.
            var knownIds = await FindManyInOdooAsync(
                AppSettings.Odoo.AccountModel, "gl_account_id",
                pending.Select(p => (object)p.Id).ToList(), cancellationToken);

            foreach (var (id, values) in pending)
            {
                try
                {
                    await UpsertInOdooAsync(
                        AppSettings.Odoo.AccountModel, "gl_account_id", id, values,
                        $"glaccount:{id}", cancellationToken, knownIds);
                    processed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    logger.LogError(ex, "Failed to process GL Account {Id}. Skipping.", id);
                }
            }

            await ArchiveMissingAsync(presentIds, cancellationToken);

            SyncState.MarkGLAccountSuccess(processed, [.. presentIds]);

            if (failed > 0)
            {
                string msg = $"{failed} GL account(s) failed to sync. See log for details.";
                SyncState.MarkGLAccountError(msg);
                logger.LogWarning(msg);
            }

            logger.LogInformation("GL Account poll completed. Processed {Count} record(s), {Failed} failure(s)", processed, failed);
        }
        catch (Exception ex)
        {
            SyncState.MarkGLAccountError(ex.Message);
            logger.LogError(ex, "Error polling GL Account changes");
            throw;
        }
    }

    private async Task ArchiveMissingAsync(List<long> presentIds, CancellationToken cancellationToken)
    {
        long[]? previouslyPresent = SyncState.LastSyncedGLAccountIds;
        if (previouslyPresent == null || previouslyPresent.Length == 0) return;

        var present = new HashSet<long>(presentIds);
        foreach (long id in previouslyPresent)
        {
            if (present.Contains(id)) continue;
            try
            {
                string cacheKey = $"glaccount:{id}";
                await ArchiveInOdooAsync(AppSettings.Odoo.AccountModel, "gl_account_id", id, cacheKey, cancellationToken);
                logger.LogInformation("Archived GL Account {Id} - no longer present in Mitchell1", id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to archive removed GL Account {Id}", id);
            }
        }
    }

    private static Dictionary<string, object> BuildValues(SqlDataReader reader, HashSet<string> columns, long id)
    {
        var values = new Dictionary<string, object>
        {
            ["gl_account_id"] = id,
            ["active"] = true
        };

        if (TryGetInt(reader, columns, "AccountClassId", out int? accountClassId)) values["account_class_id"] = accountClassId!.Value;
        if (TryGetString(reader, columns, "GLCode", out string? glCode)) values["gl_code"] = glCode!;
        if (TryGetString(reader, columns, "Description", out string? description)) values["description"] = description!;
        if (TryGetInt(reader, columns, "EntryTypeId", out int? entryTypeId)) values["entry_type_id"] = entryTypeId!.Value;
        if (TryGetString(reader, columns, "EntryTypeDescription", out string? entryTypeDescription)) values["entry_type_description"] = entryTypeDescription!;
        if (TryGetDateTime(reader, columns, "LastChangeDate", out DateTime? lastChangeDate)) values["last_change_date"] = lastChangeDate!.Value.ToString("yyyy-MM-dd HH:mm:ss");

        return values;
    }
}
