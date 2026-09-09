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
    /// Handles a per-row failure. Returns true if the cursor should be held so the record
    /// is retried on the next poll, or false once the record has exhausted its attempts and
    /// been quarantined — at which point the cursor is allowed past it so the rest of the
    /// queue keeps flowing.
    /// </summary>
    protected bool ShouldHoldCursorAfterFailure(string entityName, long id, Exception ex, ILogger logger)
    {
        int attempts = Failures.RecordFailure(entityName, id, ex.Message);

        if (attempts < MaxSyncAttempts)
        {
            logger.LogError(ex, "Failed to process {Entity} {Id} (attempt {Attempt} of {Max}). Holding cursor; will retry next poll.",
                entityName, id, attempts, MaxSyncAttempts);
            return true;
        }

        logger.LogError(ex, "Failed to process {Entity} {Id} {Attempt} times. QUARANTINED - advancing the cursor past it so the queue is not blocked. Fix the record and it will sync the next time it changes.",
            entityName, id, attempts);
        return false;
    }

    /// <summary>Clears a record's failure history after a successful sync.</summary>
    protected void MarkRowSucceeded(string entityName, long id) => Failures.Clear(entityName, id);

    /// <summary>Appends any quarantine summary to a poll's error message.</summary>
    protected string? QuarantineSummary() => Failures.Describe(MaxSyncAttempts);

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

    protected async Task<int?> FindInOdooAsync(string model, string domainField, object domainValue, CancellationToken ct)
    {
        object[] domain = { new object[] { domainField, "=", domainValue } };
        var kwargs = new Dictionary<string, object> { ["fields"] = new[] { "id" }, ["limit"] = 1 };
        var result = await OdooRpcClient.ExecuteKwAsync(model, "search_read", [domain], kwargs, ct);
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0) return null;
        return result[0].GetProperty("id").GetInt32();
    }

    protected async Task UpsertInOdooAsync(
        string model, string domainField, object domainValue,
        Dictionary<string, object> values, string cacheKey, CancellationToken ct)
    {
        if (!HashCache.HasChanged(cacheKey, values)) return;
        int? existingId = await FindInOdooAsync(model, domainField, domainValue, ct);
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
        var archiveValues = new Dictionary<string, object> { ["active"] = false };
        if (!HashCache.HasChanged(cacheKey, archiveValues)) return;
        int? existingId = await FindInOdooAsync(model, domainField, domainValue, ct);
        if (!existingId.HasValue) return;
        await OdooRpcClient.ExecuteKwAsync(model, "write", new object[] { new[] { existingId.Value }, archiveValues }, null, ct);
        HashCache.Commit(cacheKey, archiveValues);
    }

    // ── Flat entity poll template ────────────────────────────────────────────
    // Used by Customer, Vendor, and Part — all flat single-table entities.
    // Returns (newestChangeDate, newestId, processedCount). SQL-level exceptions propagate
    // to the caller; per-row exceptions are caught and logged so the batch continues.

    protected async Task<(DateTime? newestChangeDate, long? newestId, int processed)> PollFlatEntityAsync(
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
        DateTime? newestChangeDate = null;
        long? newestId = null;
        // A failed row holds the cursor; otherwise a later success in the same batch
        // moves past it and the failed record is never queried again.
        bool cursorBlocked = false;

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
            try
            {
                id = Convert.ToInt64(reader[sqlIdColumn]);
                DateTime changeDate = ParseChangeDate(reader, changeDateOrdinal, id, entityName, logger);
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
                if (!cursorBlocked)
                {
                    newestChangeDate = changeDate;
                    newestId = id;
                }
            }
            catch (Exception ex)
            {
                if (ShouldHoldCursorAfterFailure(entityName, id, ex, logger)) cursorBlocked = true;
            }
        }

        return (newestChangeDate, newestId, processed);
    }
}
