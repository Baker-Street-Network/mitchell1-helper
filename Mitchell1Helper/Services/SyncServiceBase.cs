using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public abstract class SyncServiceBase(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    RecordHashCache hashCache,
    SyncFailureTracker failureTracker)
{
    protected AppSettings AppSettings => appSettings;
    protected OdooRpcClient OdooRpcClient => odooRpcClient;
    protected SyncState SyncState => syncState;
    protected RecordHashCache HashCache => hashCache;
    protected SyncFailureTracker Failures => failureTracker;

    protected int MaxSyncAttempts => Math.Max(1, AppSettings.Sync.MaxSyncAttempts);

    /// <summary>
    /// Tracks how far a poll may move its cursor.
    /// <para>
    /// A row that failed but still has retries left blocks the cursor, so it and everything
    /// after it are re-read on the next poll. A quarantined row instead steps the cursor past
    /// itself — otherwise it is re-read every poll forever and nothing behind it ever syncs,
    /// which is the exact dam quarantine exists to prevent.
    /// </para>
    /// </summary>
    protected sealed class CursorTracker
    {
        private bool _blocked;

        public DateTime? ChangeDate { get; private set; }
        public long? Id { get; private set; }

        public bool HasAdvanced => ChangeDate.HasValue && Id.HasValue;

        public void Advance(DateTime changeDate, long id)
        {
            if (_blocked) return;
            ChangeDate = changeDate;
            Id = id;
        }

        public void Block() => _blocked = true;
    }

    /// <summary>
    /// Handles a per-row failure: records the attempt and decides whether <paramref name="cursor"/>
    /// holds at this row or steps past it.
    /// <para>
    /// <paramref name="changeDate"/> is null when the row's id or change date could not even be
    /// read; there is then nowhere to step to, so the cursor has to hold.
    /// </para>
    /// </summary>
    protected void HandleRowFailure(
        string entityName, long id, DateTime? changeDate, CursorTracker cursor, Exception ex, ILogger logger)
    {
        int attempts = Failures.RecordFailure(entityName, id, ex.Message);

        if (attempts < MaxSyncAttempts)
        {
            logger.LogError(ex, "Failed to process {Entity} {Id} (attempt {Attempt} of {Max}). Holding cursor; will retry next poll.",
                entityName, id, attempts, MaxSyncAttempts);
            cursor.Block();
            return;
        }

        if (!changeDate.HasValue)
        {
            logger.LogError(ex, "Failed to process {Entity} {Id} {Attempt} times and its change date could not be read, so the cursor cannot step past it. Holding.",
                entityName, id, attempts);
            cursor.Block();
            return;
        }

        cursor.Advance(changeDate.Value, id);
        logger.LogError(ex, "Failed to process {Entity} {Id} {Attempt} times. QUARANTINED - stepped the cursor past it so the queue is not blocked. Fix the record and it will sync the next time it changes.",
            entityName, id, attempts);
    }

    /// <summary>Clears a record's failure history after a successful sync.</summary>
    protected void MarkRowSucceeded(string entityName, long id) => Failures.Clear(entityName, id);

    /// <summary>
    /// Quarantine summary for the given entities, or null when none of them have quarantined
    /// records. Always pass the entities the calling poll owns: an unscoped summary reports one
    /// entity's bad records as every other entity's error too.
    /// </summary>
    protected string? QuarantineSummary(params string[] entityNames) => Failures.Describe(MaxSyncAttempts, entityNames);

    private static readonly Regex IdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    // ── SQL helpers ──────────────────────────────────────────────────────────

    protected static string EnsureTrustServerCertificate(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { TrustServerCertificate = true };
        return builder.ConnectionString;
    }

    protected static string QuoteIdentifier(string identifier)
    {
        if (identifier.Contains('.'))
        {
            string[] parts = identifier.Split('.');
            if (parts.Length != 2)
                throw new InvalidOperationException($"Invalid SQL identifier: {identifier}");
            foreach (string part in parts)
                if (!IdentifierRegex.IsMatch(part))
                    throw new InvalidOperationException($"Invalid SQL identifier part: {part}");
            return $"[{parts[0]}].[{parts[1]}]";
        }
        if (!IdentifierRegex.IsMatch(identifier))
            throw new InvalidOperationException($"Invalid SQL identifier: {identifier}");
        return $"[{identifier}]";
    }

    protected static string BuildCursorQuery(string quotedTable, string quotedIdCol, string quotedChangeDateCol) => $"""
        SELECT TOP (@BatchSize) *,
               CONVERT(NVARCHAR(50), {quotedChangeDateCol}, 127) AS LastChangeDate_String
        FROM {quotedTable}
        WHERE ({quotedChangeDateCol} > @LastChangeDate)
           OR ({quotedChangeDateCol} = @LastChangeDate AND {quotedIdCol} > @LastId)
        ORDER BY {quotedChangeDateCol} ASC, {quotedIdCol} ASC;
        """;

    protected static (HashSet<string> columns, int changeDateOrdinal) ReadColumnSchema(SqlDataReader reader)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int changeDateOrdinal = -1;
        foreach (var col in reader.GetColumnSchema())
        {
            if (string.IsNullOrWhiteSpace(col.ColumnName)) continue;
            columns.Add(col.ColumnName);
            if (col.ColumnName.Equals("LastChangeDate_String", StringComparison.OrdinalIgnoreCase))
                changeDateOrdinal = col.ColumnOrdinal ?? -1;
        }
        return (columns, changeDateOrdinal);
    }

    protected static DateTime ParseChangeDate(SqlDataReader reader, int changeDateOrdinal, long recordId, string entityName, ILogger logger)
    {
        try
        {
            if (changeDateOrdinal < 0 || reader.IsDBNull(changeDateOrdinal))
            {
                logger.LogWarning("{Entity} {Id}: LastChangeDate_String missing or null. Using UTC now.", entityName, recordId);
                return DateTime.UtcNow;
            }
            string dateString = reader.GetString(changeDateOrdinal);
            if (!DateTime.TryParse(dateString, out DateTime parsed))
            {
                logger.LogWarning("{Entity} {Id}: Could not parse date '{Date}'. Using UTC now.", entityName, recordId, dateString);
                return DateTime.UtcNow;
            }
            DateTime changeDate = parsed.Kind == DateTimeKind.Utc ? parsed : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            if (!Mitchell1Schema.IsValidSqlDateTime(changeDate))
            {
                logger.LogWarning("{Entity} {Id}: Date {Date} out of SQL range. Clamping.", entityName, recordId, changeDate);
                changeDate = Mitchell1Schema.ClampToValidRange(changeDate);
            }
            return changeDate;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Entity} {Id}: Failed to read LastChangeDate. Using UTC now.", entityName, recordId);
            return DateTime.UtcNow;
        }
    }

    // ── Type conversion ──────────────────────────────────────────────────────

    protected static bool ConvertToBoolean(object? value) => value switch
    {
        null or DBNull => false,
        bool b => b,
        byte by => by != 0,
        short s => s != 0,
        int i => i != 0,
        long l => l != 0,
        string str => str.Equals("1", StringComparison.OrdinalIgnoreCase)
                   || str.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || str.Equals("y", StringComparison.OrdinalIgnoreCase)
                   || str.Equals("yes", StringComparison.OrdinalIgnoreCase),
        _ => Convert.ToBoolean(value)
    };

    // ── Snake case ───────────────────────────────────────────────────────────

    protected static Dictionary<string, object> ConvertKeysToSnakeCase(Dictionary<string, object> values)
    {
        var result = new Dictionary<string, object>(values.Count);
        foreach (var (key, value) in values)
            result[ToSnakeCase(key)] = value;
        return result;
    }

    protected static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        // Normalise known multi-char acronyms to title-case before splitting on camelCase boundaries.
        foreach (string a in new[] { "MPLG", "UTC", "URL", "PO", "RO", "ID" })
            input = input.Replace(a, char.ToUpper(a[0]) + a[1..].ToLower());
        var sb = new StringBuilder(input.Length + 4);
        for (int i = 0; i < input.Length; i++)
        {
            if (i > 0 && char.IsUpper(input[i]) &&
                (i == input.Length - 1 || !char.IsUpper(input[i + 1]) || !char.IsUpper(input[i - 1])))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(input[i]));
        }
        return sb.ToString();
    }

    // ── SqlDataReader helpers ────────────────────────────────────────────────

    protected static string? GetString(SqlDataReader reader, HashSet<string> columns, string column)
    {
        if (!columns.Contains(column)) return null;
        object value = reader[column];
        return value == DBNull.Value ? null : Convert.ToString(value);
    }

    protected static bool TryGetString(SqlDataReader reader, HashSet<string> columns, string column, out string? value)
    {
        value = null;
        if (!columns.Contains(column)) return false;
        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return false;
        value = reader.GetString(ordinal);
        return !string.IsNullOrWhiteSpace(value);
    }

    protected static bool TryGetInt(SqlDataReader reader, HashSet<string> columns, string column, out int? value)
    {
        value = null;
        if (!columns.Contains(column)) return false;
        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return false;
        value = reader.GetInt32(ordinal);
        return true;
    }

    protected static bool TryGetLong(SqlDataReader reader, HashSet<string> columns, string column, out long? value)
    {
        value = null;
        if (!columns.Contains(column)) return false;
        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return false;
        value = reader.GetInt64(ordinal);
        return true;
    }

    protected static bool TryGetDecimal(SqlDataReader reader, HashSet<string> columns, string column, out decimal? value)
    {
        value = null;
        if (!columns.Contains(column)) return false;
        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return false;
        value = reader.GetDecimal(ordinal);
        return true;
    }

    protected static bool TryGetDateTime(SqlDataReader reader, HashSet<string> columns, string column, out DateTime? value)
    {
        value = null;
        if (!columns.Contains(column)) return false;
        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return false;
        value = reader.GetDateTime(ordinal);
        return true;
    }

    protected static bool TryGetBool(SqlDataReader reader, HashSet<string> columns, string column, out bool value)
    {
        value = false;
        if (!columns.Contains(column)) return false;
        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return false;
        value = ConvertToBoolean(reader.GetValue(ordinal));
        return true;
    }

    protected static void AddValue(
        Dictionary<string, object> values, SqlDataReader reader, HashSet<string> columns,
        string columnName, bool required, object? defaultValue = null)
    {
        if (!columns.Contains(columnName))
        {
            if (required && defaultValue != null) values[columnName] = defaultValue;
            return;
        }
        object value = reader[columnName];
        if (value == DBNull.Value)
        {
            if (defaultValue != null) values[columnName] = defaultValue;
            else if (required) throw new InvalidOperationException($"Required field {columnName} is null");
            // skip null optional values
        }
        else if (value is DateTime dt) values[columnName] = dt.ToString("yyyy-MM-dd HH:mm:ss");
        else if (value is byte[]) { /* skip SQL Server timestamp/rowversion */ }
        else values[columnName] = value;
    }

    // ── Odoo operations ──────────────────────────────────────────────────────

    // Odoo hides archived records from search by default. This sync owns the active flag, so
    // every lookup has to see them — otherwise an archived record reads as missing and a blind
    // create duplicates it.
    private static readonly Dictionary<string, object> IncludeArchivedContext = new() { ["active_test"] = false };

    protected async Task<int?> FindInOdooAsync(string model, string domainField, object domainValue, CancellationToken ct)
    {
        object[] domain = { new object[] { domainField, "=", domainValue } };
        var kwargs = new Dictionary<string, object>
        {
            ["fields"] = new[] { "id" },
            ["limit"] = 1,
            ["context"] = IncludeArchivedContext
        };
        var result = await OdooRpcClient.ExecuteKwAsync(model, "search_read", [domain], kwargs, ct);
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0) return null;
        return result[0].GetProperty("id").GetInt32();
    }

    /// <summary>
    /// Resolves many Mitchell1 keys to Odoo ids in one call, keyed by the key's string form.
    /// Lets a poll that re-reads the same rows every cycle verify they still exist in Odoo
    /// without one round-trip per record.
    /// </summary>
    protected async Task<Dictionary<string, int>> FindManyInOdooAsync(
        string model, string domainField, IReadOnlyCollection<object> domainValues, CancellationToken ct)
    {
        var found = new Dictionary<string, int>(domainValues.Count, StringComparer.Ordinal);
        if (domainValues.Count == 0) return found;

        object[] domain = { new object[] { domainField, "in", domainValues.ToArray() } };
        var kwargs = new Dictionary<string, object>
        {
            ["fields"] = new[] { "id", domainField },
            ["context"] = IncludeArchivedContext
        };
        var result = await OdooRpcClient.ExecuteKwAsync(model, "search_read", [domain], kwargs, ct);
        if (result.ValueKind != JsonValueKind.Array) return found;

        foreach (JsonElement record in result.EnumerateArray())
        {
            if (!record.TryGetProperty(domainField, out JsonElement key)) continue;
            string? keyText = key.ValueKind switch
            {
                JsonValueKind.String => key.GetString(),
                JsonValueKind.Number => key.GetRawText(),
                _ => null
            };
            if (keyText != null) found[keyText] = record.GetProperty("id").GetInt32();
        }

        return found;
    }

    /// <summary>
    /// Creates or updates the record in Odoo.
    /// <para>
    /// Whether Odoo still holds the record is resolved before the hash is trusted. A matching hash
    /// only proves these values were accepted once; it says nothing about the record still being
    /// there. If it was deleted, or the database was restored from a backup, skipping on the hash
    /// alone leaves it missing forever — and every record that references it failing forever.
    /// </para>
    /// <para>
    /// <paramref name="knownIds"/> is an optional id map from <see cref="FindManyInOdooAsync"/>,
    /// used in place of a per-record lookup.
    /// </para>
    /// </summary>
    protected async Task UpsertInOdooAsync(
        string model, string domainField, object domainValue,
        Dictionary<string, object> values, string cacheKey, CancellationToken ct,
        IReadOnlyDictionary<string, int>? knownIds = null)
    {
        bool changed = HashCache.HasChanged(cacheKey, values);

        int? existingId = knownIds != null
            ? knownIds.TryGetValue(domainValue.ToString() ?? string.Empty, out int known) ? known : null
            : await FindInOdooAsync(model, domainField, domainValue, ct);

        // Unchanged and still present is the only case with nothing to do.
        if (!changed && existingId.HasValue) return;

        if (existingId.HasValue)
            await OdooRpcClient.ExecuteKwAsync(model, "write", [new[] { existingId.Value }, values], null, ct);
        else
            await OdooRpcClient.ExecuteKwAsync(model, "create", [values], null, ct);
        // Only after Odoo accepted the write — a failed call must stay dirty so it retries.
        HashCache.Commit(cacheKey, values);
    }

    protected async Task ArchiveInOdooAsync(
        string model, string domainField, object domainValue, string cacheKey, CancellationToken ct)
    {
        // Looked up before the hash is consulted, for the same reason as UpsertInOdooAsync: the
        // hash cannot tell us whether Odoo still has the record, or whether it was un-archived
        // there since we last wrote it.
        int? existingId = await FindInOdooAsync(model, domainField, domainValue, ct);
        if (!existingId.HasValue)
        {
            // Nothing to archive. Drop any stale hash so the record is re-sent in full if it
            // ever comes back.
            HashCache.Remove(cacheKey);
            return;
        }

        var archiveValues = new Dictionary<string, object> { ["active"] = false };
        await OdooRpcClient.ExecuteKwAsync(model, "write", [new[] { existingId.Value }, archiveValues], null, ct);
        HashCache.Commit(cacheKey, archiveValues);
    }

    // ── Flat entity poll template ────────────────────────────────────────────
    // Used by Customer, Vendor, and Part — all flat single-table entities.
    // Returns the cursor the caller should persist plus processed/failed counts. SQL-level
    // exceptions propagate to the caller; per-row exceptions are caught and logged so the
    // batch continues.

    protected async Task<(CursorTracker cursor, int processed, int failed)> PollFlatEntityAsync(
        string entityName,
        string sqlTable,
        string sqlIdColumn,
        string? sqlIsDeletedColumn,
        string odooModel,
        string odooDomainField,
        string cacheKeyPrefix,
        bool useSnakeCase,
        DateTime lastChangeDate,
        long lastId,
        Func<SqlDataReader, HashSet<string>, long, object, Dictionary<string, object>> buildValues,
        Func<long, object> getDomainValue,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        int processed = 0;
        int failed = 0;
        var cursor = new CursorTracker();

        string cs = EnsureTrustServerCertificate(AppSettings.Sql.ConnectionString);
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(cancellationToken);

        string table = QuoteIdentifier(sqlTable);
        string idCol = QuoteIdentifier(sqlIdColumn);
        string changeDateCol = QuoteIdentifier(Mitchell1Schema.LastChangeDateColumn);

        await using var cmd = new SqlCommand(BuildCursorQuery(table, idCol, changeDateCol), conn);
        cmd.Parameters.AddWithValue("@BatchSize", AppSettings.Sync.BatchSize);
        cmd.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = lastChangeDate;
        cmd.Parameters.AddWithValue("@LastId", lastId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var (columns, changeDateOrdinal) = ReadColumnSchema(reader);

        while (await reader.ReadAsync(cancellationToken))
        {
            long id = 0;
            DateTime? changeDate = null;
            try
            {
                id = Convert.ToInt64(reader[sqlIdColumn]);
                changeDate = ParseChangeDate(reader, changeDateOrdinal, id, entityName, logger);
                object domainValue = getDomainValue(id);
                string cacheKey = $"{cacheKeyPrefix}:{domainValue}";

                bool isDeleted = false;
                if (sqlIsDeletedColumn != null)
                    isDeleted = TryGetBool(reader, columns, sqlIsDeletedColumn, out bool isDel) && isDel;
                if (isDeleted)
                {
                    await ArchiveInOdooAsync(odooModel, odooDomainField, domainValue, cacheKey, cancellationToken);
                }
                else
                {
                    var values = buildValues(reader, columns, id, domainValue);
                    if (useSnakeCase) values = ConvertKeysToSnakeCase(values);
                    await UpsertInOdooAsync(odooModel, odooDomainField, domainValue, values, cacheKey, cancellationToken);
                }

                MarkRowSucceeded(entityName, id);
                processed++;
                cursor.Advance(changeDate.Value, id);
            }
            catch (Exception ex)
            {
                failed++;
                HandleRowFailure(entityName, id, changeDate, cursor, ex, logger);
            }
        }

        return (cursor, processed, failed);
    }
}
