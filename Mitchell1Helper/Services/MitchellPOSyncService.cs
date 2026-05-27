using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class MitchellPOSyncService(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    ILogger<MitchellPOSyncService> logger)
{
    private static readonly Regex IdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private DateTime _lastChangeDateUtc = syncState.LastProcessedPOChangeDateUtc ?? DateTime.MinValue;
    private long _lastPOMasterId = syncState.LastProcessedPOMasterId ?? 0;

    /// <summary>
    /// Converts PascalCase field names to snake_case for Odoo compatibility.
    /// Handles acronyms correctly: POMasterId -> po_master_id (not p_o_master_id)
    /// </summary>
    private static string ToSnakeCase(string pascalCase)
    {
        if (string.IsNullOrEmpty(pascalCase))
            return pascalCase;

        var result = new System.Text.StringBuilder();

        for (int i = 0; i < pascalCase.Length; i++)
        {
            char current = pascalCase[i];
            bool isUpper = char.IsUpper(current);

            // Add underscore before uppercase letter if:
            // 1. Not the first character
            // 2. Previous character is lowercase OR next character is lowercase (end of acronym)
            if (i > 0 && isUpper)
            {
                char prev = pascalCase[i - 1];
                bool prevIsLower = char.IsLower(prev);

                // Check if this is the end of an acronym (e.g., "POMaster" -> M is end of "PO")
                bool endOfAcronym = false;
                if (i + 1 < pascalCase.Length)
                {
                    char next = pascalCase[i + 1];
                    endOfAcronym = char.IsUpper(prev) && char.IsLower(next);
                }

                if (prevIsLower || endOfAcronym)
                {
                    result.Append('_');
                }
            }

            result.Append(char.ToLowerInvariant(current));
        }

        return result.ToString();
    }

    /// <summary>
    /// Converts dictionary keys from PascalCase to snake_case for Odoo.
    /// </summary>
    private static Dictionary<string, object> ConvertKeysToSnakeCase(Dictionary<string, object> pascalCaseDict)
    {
        var snakeCaseDict = new Dictionary<string, object>();
        foreach (var (key, value) in pascalCaseDict)
        {
            string snakeCaseKey = ToSnakeCase(key);
            snakeCaseDict[snakeCaseKey] = value;
        }
        return snakeCaseDict;
    }

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
            syncState.MarkPOError(message);
            logger.LogWarning(message);
            return;
        }

        logger.LogInformation("Starting PO poll. Last processed: ChangeDate={LastChangeDate:O}, POMasterId={LastPOMasterId}",
            _lastChangeDateUtc, _lastPOMasterId);

        int processedHeaders = 0;
        int processedDetails = 0;
        DateTime? newestChangeDate = null;
        long? newestPOMasterId = null;

        try
        {
            string connectionString = EnsureTrustServerCertificate(appSettings.Sql.ConnectionString);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Sync PO Headers
            string masterTable = QuoteIdentifier(Mitchell1Schema.POMasterTable);
            string masterIdColumn = QuoteIdentifier(Mitchell1Schema.POMasterIdColumn);
            string changeDateColumn = QuoteIdentifier(Mitchell1Schema.LastChangeDateColumn);

            string headerQuery = $@"
SELECT TOP (@BatchSize) *, 
       CONVERT(NVARCHAR(50), {changeDateColumn}, 127) AS LastChangeDate_String
FROM {masterTable}
WHERE ({changeDateColumn} > @LastChangeDate)
   OR ({changeDateColumn} = @LastChangeDate AND {masterIdColumn} > @LastPOMasterId)
ORDER BY {changeDateColumn} ASC, {masterIdColumn} ASC;";

            await using var headerCommand = new SqlCommand(headerQuery, connection);
            headerCommand.Parameters.AddWithValue("@BatchSize", appSettings.Sync.BatchSize);
            headerCommand.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = _lastChangeDateUtc;
            headerCommand.Parameters.AddWithValue("@LastPOMasterId", _lastPOMasterId);

            await using SqlDataReader headerReader = await headerCommand.ExecuteReaderAsync(cancellationToken);

            var headerColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int changeDateStringOrdinal = -1;
            foreach (var column in headerReader.GetColumnSchema())
            {
                if (!string.IsNullOrWhiteSpace(column.ColumnName))
                {
                    headerColumns.Add(column.ColumnName);
                    if (column.ColumnName.Equals("LastChangeDate_String", StringComparison.OrdinalIgnoreCase))
                    {
                        changeDateStringOrdinal = column.ColumnOrdinal ?? -1;
                    }
                }
            }

            while (await headerReader.ReadAsync(cancellationToken))
            {
                long poMasterId = 0;
                try
                {
                    poMasterId = Convert.ToInt64(headerReader[Mitchell1Schema.POMasterIdColumn]);

                    DateTime changeDate = ParseChangeDate(headerReader, changeDateStringOrdinal, poMasterId, "PO Header");

                    Dictionary<string, object> headerValues = BuildPOHeaderValues(headerReader, headerColumns);
                    await UpsertPOHeaderInOdooAsync(headerValues, cancellationToken);

                    processedHeaders++;
                    newestChangeDate = changeDate;
                    newestPOMasterId = poMasterId;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to process PO header {POMasterId}. Skipping this record and continuing.",
                        poMasterId);
                }
            }

            await headerReader.CloseAsync();

            // Sync PO Details independently
            processedDetails = await SyncPODetailsAsync(connection, cancellationToken);

            if (newestChangeDate.HasValue && newestPOMasterId.HasValue)
            {
                _lastChangeDateUtc = newestChangeDate.Value;
                _lastPOMasterId = newestPOMasterId.Value;
                syncState.MarkPOSuccess(newestChangeDate, newestPOMasterId, processedHeaders, processedDetails);
            }

            logger.LogInformation("PO polling completed. Processed {HeaderCount} header(s) and {DetailCount} detail(s)",
                processedHeaders, processedDetails);
        }
        catch (Exception ex)
        {
            syncState.MarkPOError(ex.Message);
            logger.LogError(ex, "Error polling Mitchell1 PO changes");
            throw;
        }
    }

    private async Task<int> SyncPODetailsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        int processedDetails = 0;

        string detailTable = QuoteIdentifier(Mitchell1Schema.PODetailTable);
        string detailIdColumn = QuoteIdentifier(Mitchell1Schema.PODetailIdColumn);
        string changeDateColumn = QuoteIdentifier(Mitchell1Schema.LastChangeDateColumn);

        // Get details that have changed (based on their own LastChangeDate)
        // This ensures we sync details independently of header changes
        string detailQuery = $@"
SELECT TOP (@BatchSize) *, 
       CONVERT(NVARCHAR(50), {changeDateColumn}, 127) AS LastChangeDate_String
FROM {detailTable}
WHERE {changeDateColumn} > @LastChangeDate
ORDER BY {changeDateColumn} ASC, {detailIdColumn} ASC;";

        await using var detailCommand = new SqlCommand(detailQuery, connection);
        detailCommand.Parameters.AddWithValue("@BatchSize", appSettings.Sync.BatchSize * 10); // More details than headers typically
        detailCommand.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = syncState.LastProcessedPODetailChangeDateUtc ?? DateTime.MinValue;

        await using SqlDataReader detailReader = await detailCommand.ExecuteReaderAsync(cancellationToken);

        var detailColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int changeDateStringOrdinal = -1;
        foreach (var column in detailReader.GetColumnSchema())
        {
            if (!string.IsNullOrWhiteSpace(column.ColumnName))
            {
                detailColumns.Add(column.ColumnName);
                if (column.ColumnName.Equals("LastChangeDate_String", StringComparison.OrdinalIgnoreCase))
                {
                    changeDateStringOrdinal = column.ColumnOrdinal ?? -1;
                }
            }
        }

        DateTime? newestDetailChangeDate = null;
        long? newestDetailId = null;

        while (await detailReader.ReadAsync(cancellationToken))
        {
            long poDetailId = 0;
            try
            {
                poDetailId = Convert.ToInt64(detailReader[Mitchell1Schema.PODetailIdColumn]);

                DateTime changeDate = ParseChangeDate(detailReader, changeDateStringOrdinal, poDetailId, "PO Detail");

                Dictionary<string, object> detailValues = BuildPODetailValues(detailReader, detailColumns);
                await UpsertPODetailInOdooAsync(detailValues, cancellationToken);

                processedDetails++;
                newestDetailChangeDate = changeDate;
                newestDetailId = poDetailId;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to process PO detail {PODetailId}. Skipping this record and continuing.",
                    poDetailId);
            }
        }

        // Update detail-specific sync state
        if (newestDetailChangeDate.HasValue && newestDetailId.HasValue)
        {
            syncState.MarkPODetailSuccess(newestDetailChangeDate, newestDetailId);
        }

        return processedDetails;
    }

    private DateTime ParseChangeDate(SqlDataReader reader, int changeDateStringOrdinal, long recordId, string recordType)
    {
        try
        {
            string? dateString = null;
            if (changeDateStringOrdinal >= 0)
            {
                object val = reader.GetValue(changeDateStringOrdinal);
                dateString = val == DBNull.Value ? null : val?.ToString();
            }
            else
            {
                logger.LogWarning("{RecordType} {RecordId}: LastChangeDate_String column not found in result set!", recordType, recordId);
            }

            if (string.IsNullOrWhiteSpace(dateString))
            {
                logger.LogWarning("NULL or empty LastChangeDate for {RecordType} {RecordId}. Using current UTC time.", recordType, recordId);
                return DateTime.UtcNow;
            }

            if (DateTime.TryParse(dateString, out DateTime parsed))
            {
                DateTime changeDate = parsed.Kind == DateTimeKind.Utc ? parsed : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

                if (!Mitchell1Schema.IsValidSqlDateTime(changeDate))
                {
                    logger.LogWarning(
                        "Invalid date {Date} (from string: '{DateString}') for {RecordType} {RecordId}. Clamping to valid range.",
                        changeDate, dateString, recordType, recordId);
                    changeDate = Mitchell1Schema.ClampToValidRange(changeDate);
                }

                return changeDate;
            }

            logger.LogWarning(
                "Could not parse LastChangeDate string '{DateString}' for {RecordType} {RecordId}. Using current UTC time.",
                dateString, recordType, recordId);
            return DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to read LastChangeDate for {RecordType} {RecordId}. Using current UTC time instead.",
                recordType, recordId);
            return DateTime.UtcNow;
        }
    }

    private async Task UpsertPOHeaderInOdooAsync(
        Dictionary<string, object> values,
        CancellationToken cancellationToken)
    {
        if (!values.TryGetValue("POMasterId", out object? poMasterIdObj) || poMasterIdObj == null)
        {
            logger.LogWarning("Cannot upsert PO header without POMasterId");
            return;
        }

        int poMasterId = Convert.ToInt32(poMasterIdObj);

        var existingRecord = await FindPOHeaderByIdAsync(poMasterId, cancellationToken);

        // Convert PascalCase keys to snake_case for Odoo
        var odooValues = ConvertKeysToSnakeCase(values);

        if (existingRecord.HasValue)
        {
            int existingId = existingRecord.Value.id;
            var existingValues = existingRecord.Value.values;

            if (HasChanges(odooValues, existingValues))
            {
                await odooRpcClient.ExecuteKwAsync(
                    appSettings.Odoo.POHeaderModel,
                    "write",
                    new object[] { new[] { existingId }, odooValues },
                    null,
                    cancellationToken);
            }
            return;
        }

        await odooRpcClient.ExecuteKwAsync(
            appSettings.Odoo.POHeaderModel,
            "create",
            new object[] { odooValues },
            null,
            cancellationToken);
    }

    private async Task UpsertPODetailInOdooAsync(
        Dictionary<string, object> values,
        CancellationToken cancellationToken)
    {
        if (!values.TryGetValue("PODetailId", out object? poDetailIdObj) || poDetailIdObj == null)
        {
            logger.LogWarning("Cannot upsert PO detail without PODetailId");
            return;
        }

        int poDetailId = Convert.ToInt32(poDetailIdObj);

        var existingRecord = await FindPODetailByIdAsync(poDetailId, cancellationToken);

        // Convert PascalCase keys to snake_case for Odoo
        var odooValues = ConvertKeysToSnakeCase(values);

        if (existingRecord.HasValue)
        {
            int existingId = existingRecord.Value.id;
            var existingValues = existingRecord.Value.values;

            if (HasChanges(odooValues, existingValues))
            {
                await odooRpcClient.ExecuteKwAsync(
                    appSettings.Odoo.PODetailModel,
                    "write",
                    new object[] { new[] { existingId }, odooValues },
                    null,
                    cancellationToken);
            }
            return;
        }

        await odooRpcClient.ExecuteKwAsync(
            appSettings.Odoo.PODetailModel,
            "create",
            new object[] { odooValues },
            null,
            cancellationToken);
    }

    private async Task<(int id, System.Text.Json.JsonElement values)?> FindPOHeaderByIdAsync(int poMasterId, CancellationToken cancellationToken)
    {
        // Use snake_case for Odoo field name
        object[] domain = { new object[] { "po_master_id", "=", poMasterId } };
        var kwargs = new Dictionary<string, object>
        {
            ["fields"] = new[] { "id" },
            ["limit"] = 1
        };

        var result = await odooRpcClient.ExecuteKwAsync(
            appSettings.Odoo.POHeaderModel,
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

    private async Task<(int id, System.Text.Json.JsonElement values)?> FindPODetailByIdAsync(int poDetailId, CancellationToken cancellationToken)
    {
        // Use snake_case for Odoo field name
        object[] domain = { new object[] { "po_detail_id", "=", poDetailId } };
        var kwargs = new Dictionary<string, object>
        {
            ["fields"] = new[] { "id" },
            ["limit"] = 1
        };

        var result = await odooRpcClient.ExecuteKwAsync(
            appSettings.Odoo.PODetailModel,
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

    private static bool HasChanges(Dictionary<string, object> newValues, System.Text.Json.JsonElement existingValues)
    {
        foreach (var (key, newValue) in newValues)
        {
            if (!existingValues.TryGetProperty(key, out var existingValueElement))
            {
                return true;
            }

            string? newValueStr = ConvertToString(newValue);
            string? existingValueStr = ConvertToString(existingValueElement);

            if (newValueStr != existingValueStr)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ConvertToString(object? value)
    {
        if (value == null || value == DBNull.Value)
            return null;

        if (value is bool boolValue)
            return boolValue.ToString();

        if (value is DateTime dateValue)
            return dateValue.ToString("O");

        return value.ToString();
    }

    private static string? ConvertToString(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Null => null,
            System.Text.Json.JsonValueKind.True => "True",
            System.Text.Json.JsonValueKind.False => "False",
            System.Text.Json.JsonValueKind.String => element.GetString(),
            _ => element.ToString()
        };
    }

    private static Dictionary<string, object> BuildPOHeaderValues(
        SqlDataReader reader,
        HashSet<string> columns)
    {
        var values = new Dictionary<string, object>();

        // Required fields
        AddValue(values, reader, columns, "POMasterId", true);
        AddValue(values, reader, columns, "PODate", true);
        AddValue(values, reader, columns, "RequiredDate", true);
        AddValue(values, reader, columns, "POShipViaId", true);
        AddValue(values, reader, columns, "IsClosed", true);
        AddValue(values, reader, columns, "POStateId", true);
        AddValue(values, reader, columns, "RecordVersion", false, 1);
        AddValue(values, reader, columns, "LastChangeDate", true);

        // Optional fields
        AddValue(values, reader, columns, "WriterId", false);
        AddValue(values, reader, columns, "VendorId", false);
        AddValue(values, reader, columns, "CommentInternal", false);
        AddValue(values, reader, columns, "CommentReport", false);
        AddValue(values, reader, columns, "TaxTotal", false);
        AddValue(values, reader, columns, "PartsTotal", false);
        AddValue(values, reader, columns, "PartsReceived", false);
        AddValue(values, reader, columns, "BackOrder", false);
        AddValue(values, reader, columns, "Phone", false);
        AddValue(values, reader, columns, "PhoneExt", false);
        AddValue(values, reader, columns, "Taxable", false);
        AddValue(values, reader, columns, "TaxReceived", false);
        AddValue(values, reader, columns, "ClosedDate", false);
        AddValue(values, reader, columns, "ConfirmNum", false);
        AddValue(values, reader, columns, "TrackingNumber", false);

        return values;
    }

    private static Dictionary<string, object> BuildPODetailValues(
        SqlDataReader reader,
        HashSet<string> columns)
    {
        var values = new Dictionary<string, object>();

        // Required fields
        AddValue(values, reader, columns, "PODetailId", true);
        AddValue(values, reader, columns, "POMasterId", true);
        AddValue(values, reader, columns, "InventoryPart", true);
        AddValue(values, reader, columns, "LocationId", false, "");
        AddValue(values, reader, columns, "IsTire", false, false);
        AddValue(values, reader, columns, "RecordVersion", false, 1);
        AddValue(values, reader, columns, "LastChangeDate", true);

        // Optional fields
        AddValue(values, reader, columns, "Sequence", false);
        AddValue(values, reader, columns, "PartId", false);
        AddValue(values, reader, columns, "PartNumber", false);
        AddValue(values, reader, columns, "Description", false);
        AddValue(values, reader, columns, "Quantity", false);
        AddValue(values, reader, columns, "QuantityReceived", false);
        AddValue(values, reader, columns, "Cost", false);
        AddValue(values, reader, columns, "CoreCost", false);
        AddValue(values, reader, columns, "QuantityBackOrder", false);
        AddValue(values, reader, columns, "QuantityCancelled", false);
        AddValue(values, reader, columns, "Notes", false);
        AddValue(values, reader, columns, "QuantityAvailConf", false);
        AddValue(values, reader, columns, "MfgId", false);
        AddValue(values, reader, columns, "AltPartNo", false);
        AddValue(values, reader, columns, "AltMfgId", false);
        AddValue(values, reader, columns, "RepairOrderId", false);
        AddValue(values, reader, columns, "SaveDescription", false);
        AddValue(values, reader, columns, "WDLineCode", false);
        AddValue(values, reader, columns, "PartType", false);
        AddValue(values, reader, columns, "LocationName", false);
        AddValue(values, reader, columns, "ListPrice", false);
        AddValue(values, reader, columns, "CatalogVendorId", false);
        AddValue(values, reader, columns, "Metadata", false);
        AddValue(values, reader, columns, "AddedFromSupplier", false);
        AddValue(values, reader, columns, "PurchasedFromVendor", false);
        AddValue(values, reader, columns, "PurchasedFromSupplier", false);
        AddValue(values, reader, columns, "ShippingDescription", false);
        AddValue(values, reader, columns, "ShippingCost", false);

        return values;
    }

    private static void AddValue(
        Dictionary<string, object> values,
        SqlDataReader reader,
        HashSet<string> columns,
        string columnName,
        bool required,
        object? defaultValue = null)
    {
        if (!columns.Contains(columnName))
        {
            if (required && defaultValue != null)
            {
                values[columnName] = defaultValue;
            }
            return;
        }

        object value = reader[columnName];
        if (value == DBNull.Value)
        {
            if (defaultValue != null)
            {
                values[columnName] = defaultValue;
            }
            else if (!required)
            {
                // Skip null optional values - let Odoo handle defaults
                return;
            }
            else
            {
                // Required field is null - this shouldn't happen but handle gracefully
                throw new InvalidOperationException($"Required field {columnName} is null");
            }
        }
        else
        {
            // Convert specific types for Odoo compatibility
            if (value is DateTime dateTime)
            {
                // Odoo expects ISO 8601 format
                values[columnName] = dateTime.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else if (value is byte[] byteArray)
            {
                // Skip timestamp/rowversion fields - they're SQL Server internal
                return;
            }
            else
            {
                values[columnName] = value;
            }
        }
    }

    private static string QuoteIdentifier(string identifier)
    {
        // Handle schema-qualified names (e.g., "SM.POMaster")
        if (identifier.Contains('.'))
        {
            var parts = identifier.Split('.');
            if (parts.Length != 2)
            {
                throw new InvalidOperationException($"Invalid SQL identifier: {identifier}");
            }

            // Validate each part separately
            foreach (var part in parts)
            {
                if (!IdentifierRegex.IsMatch(part))
                {
                    throw new InvalidOperationException($"Invalid SQL identifier part: {part}");
                }
            }

            // Quote each part: [Schema].[Table]
            return $"[{parts[0]}].[{parts[1]}]";
        }

        // Single identifier (column name)
        if (!IdentifierRegex.IsMatch(identifier))
        {
            throw new InvalidOperationException($"Invalid SQL identifier: {identifier}");
        }

        return $"[{identifier}]";
    }
}
