using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class MitchellPartSyncService(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    RecordHashCache hashCache,
    ILogger<MitchellPartSyncService> logger)
{
    private static readonly Regex IdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private DateTime _lastChangeDateUtc = syncState.LastProcessedPartChangeDateUtc ?? DateTime.MinValue;
    private long _lastPartId = syncState.LastProcessedPartId ?? 0;

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
            syncState.MarkPartError(message);
            logger.LogWarning(message);
            return;
        }

        logger.LogInformation("Starting part poll. Last processed: ChangeDate={LastChangeDate:O}, PartId={LastPartId}",
            _lastChangeDateUtc, _lastPartId);

        int processed = 0;
        DateTime? newestChangeDate = null;
        long? newestPartId = null;

        try
        {
            string connectionString = EnsureTrustServerCertificate(appSettings.Sql.ConnectionString);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            string table = QuoteIdentifier(Mitchell1Schema.PartsTable);
            string idColumn = QuoteIdentifier(Mitchell1Schema.PartIdColumn);
            string changeDateColumn = QuoteIdentifier(Mitchell1Schema.LastChangeDateColumn);
            string isDeletedColumn = QuoteIdentifier(Mitchell1Schema.IsDeletedColumn);

            // Select all columns plus a string version of LastChangeDate to avoid SqlDateTime overflow
            string query = $@"
SELECT TOP (@BatchSize) *, 
       CONVERT(NVARCHAR(50), {changeDateColumn}, 127) AS LastChangeDate_String
FROM {table}
WHERE ({changeDateColumn} > @LastChangeDate)
   OR ({changeDateColumn} = @LastChangeDate AND {idColumn} > @LastPartId)
ORDER BY {changeDateColumn} ASC, {idColumn} ASC;";

            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@BatchSize", appSettings.Sync.BatchSize);
            command.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = _lastChangeDateUtc;
            command.Parameters.AddWithValue("@LastPartId", _lastPartId);

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
                    long partId = reader.GetInt32(reader.GetOrdinal(Mitchell1Schema.PartIdColumn));

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
                                    "Could not parse LastChangeDate string '{DateString}' for part {PartId}. Using current UTC time.",
                                    dateString, partId);
                                changeDate = DateTime.UtcNow;
                            }

                            if (!Mitchell1Schema.IsValidSqlDateTime(changeDate))
                            {
                                logger.LogWarning(
                                    "Invalid date {Date} (from string: '{DateString}') for part {PartId}. Clamping to valid range.",
                                    changeDate, dateString, partId);
                                changeDate = Mitchell1Schema.ClampToValidRange(changeDate);
                            }
                        }
                        else
                        {
                            logger.LogWarning(
                                "LastChangeDate_String column missing or null for part {PartId}. Using current UTC time.",
                                partId);
                            changeDate = DateTime.UtcNow;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Failed to read LastChangeDate for part {PartId}. Using current UTC time instead.",
                            partId);
                        changeDate = DateTime.UtcNow;
                    }

                    bool isDeleted = ConvertToBoolean(reader[Mitchell1Schema.IsDeletedColumn]);

                    if (isDeleted)
                    {
                        await ArchivePartInOdooAsync(appSettings.Odoo.PartModel, partId, cancellationToken);
                    }
                    else
                    {
                        Dictionary<string, object> values = BuildPartValues(reader, columns, partId);
                        await UpsertPartInOdooAsync(appSettings.Odoo.PartModel, partId, values, cancellationToken);
                    }

                    processed++;
                    newestChangeDate = changeDate;
                    newestPartId = partId;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to process part {PartId}. Skipping this record and continuing.",
                        reader.GetInt32(reader.GetOrdinal(Mitchell1Schema.PartIdColumn)));
                }
            }

            if (newestChangeDate.HasValue && newestPartId.HasValue)
            {
                _lastChangeDateUtc = newestChangeDate.Value;
                _lastPartId = newestPartId.Value;
                syncState.MarkPartSuccess(newestChangeDate, newestPartId, processed);
            }

            logger.LogInformation("Part polling completed. Processed {Count} part change(s)", processed);
        }
        catch (Exception ex)
        {
            syncState.MarkPartError(ex.Message);
            logger.LogError(ex, "Error polling Mitchell1 Part changes");
            throw;
        }
    }

    private async Task UpsertPartInOdooAsync(
        string model,
        long partId,
        Dictionary<string, object> values,
        CancellationToken cancellationToken)
    {
        var snakeCaseValues = ConvertKeysToSnakeCase(values);
        string cacheKey = $"part:{partId}";

        if (!hashCache.HasChangedAndUpdate(cacheKey, snakeCaseValues))
        {
            return;
        }

        int? existingId = await FindPartByIdAsync(model, partId, cancellationToken);
        if (existingId.HasValue)
        {
            await odooRpcClient.ExecuteKwAsync(
                model,
                "write",
                new object[] { new[] { existingId.Value }, snakeCaseValues },
                null,
                cancellationToken);
            logger.LogInformation("Updated part {PartId} in Odoo staging table", partId);
            return;
        }

        var result = await odooRpcClient.ExecuteKwAsync(
            model,
            "create",
            new object[] { snakeCaseValues },
            null,
            cancellationToken);
        logger.LogInformation("Created part {PartId} in Odoo staging table", partId);
    }

    private async Task ArchivePartInOdooAsync(string model, long partId, CancellationToken cancellationToken)
    {
        string cacheKey = $"part:{partId}";
        var archiveValues = new Dictionary<string, object> { ["active"] = false };

        if (!hashCache.HasChangedAndUpdate(cacheKey, archiveValues))
        {
            return;
        }

        int? existingId = await FindPartByIdAsync(model, partId, cancellationToken);
        if (!existingId.HasValue)
        {
            logger.LogDebug("Part not found in Odoo staging table for archive (part_id={PartId})", partId);
            return;
        }

        await odooRpcClient.ExecuteKwAsync(
            model,
            "write",
            new object[] { new[] { existingId.Value }, archiveValues },
            null,
            cancellationToken);
        logger.LogInformation("Archived part {PartId} in Odoo staging table", partId);
    }

    private async Task<int?> FindPartByIdAsync(
        string model,
        long partId,
        CancellationToken cancellationToken)
    {
        object[] domain = { new object[] { "part_id", "=", partId } };
        var kwargs = new Dictionary<string, object> { ["fields"] = new[] { "id" }, ["limit"] = 1 };

        var result = await odooRpcClient.ExecuteKwAsync(
            model,
            "search_read",
            new object[] { domain },
            kwargs,
            cancellationToken);

        if (result.ValueKind != System.Text.Json.JsonValueKind.Array || result.GetArrayLength() == 0)
            return null;

        return result[0].GetProperty("id").GetInt32();
    }

    private static Dictionary<string, object> BuildPartValues(
        SqlDataReader reader,
        HashSet<string> columns,
        long partId)
    {
        string? description = GetString(reader, columns, "Description");
        decimal? cost = GetDecimal(reader, columns, "Cost");
        decimal? price = GetDecimal(reader, columns, "Price");
        decimal? listPrice = GetDecimal(reader, columns, "ListPrice");
        bool taxExempt = GetBoolean(reader, columns, "TaxExempt");
        string? comment = GetString(reader, columns, "Comment");
        string? size = GetString(reader, columns, "Size");
        bool isTire = GetBoolean(reader, columns, "IsTire");
        bool isCore = GetBoolean(reader, columns, "IsCore");
        int? categoryId = GetInt(reader, columns, "CategoryId");
        int? accountClassId = GetInt(reader, columns, "AccountClassId");
        int? manufacturerId = GetInt(reader, columns, "ManufacturerId");
        bool userEnteredPrice = GetBoolean(reader, columns, "UserEnteredPrice");
        int? kitId = GetInt(reader, columns, "KitId");
        bool isMPLGItem = GetBoolean(reader, columns, "IsMPLGItem");
        bool isChangedMPLGItem = GetBoolean(reader, columns, "IsChangedMPLGItem");
        decimal? coreCost = GetDecimal(reader, columns, "CoreCost");
        decimal? coreListPrice = GetDecimal(reader, columns, "CoreListPrice");
        int? partFeeId = GetInt(reader, columns, "PartFeeId");
        int? recordVersion = GetInt(reader, columns, "RecordVersion");
        string? metadata = GetString(reader, columns, "Metadata");

        var values = new Dictionary<string, object>
        {
            ["PartId"] = partId,
            ["Active"] = true
        };

        if (!string.IsNullOrWhiteSpace(description)) values["Description"] = description;
        if (cost.HasValue) values["Cost"] = (double)cost.Value;
        if (price.HasValue) values["Price"] = (double)price.Value;
        if (listPrice.HasValue) values["ListPrice"] = (double)listPrice.Value;
        values["TaxExempt"] = taxExempt;
        if (!string.IsNullOrWhiteSpace(comment)) values["Comment"] = comment;
        if (!string.IsNullOrWhiteSpace(size)) values["Size"] = size;
        values["IsTire"] = isTire;
        values["IsCore"] = isCore;

        if (categoryId.HasValue) values["CategoryId"] = categoryId.Value;
        if (accountClassId.HasValue) values["AccountClassId"] = accountClassId.Value;
        if (manufacturerId.HasValue) values["ManufacturerId"] = manufacturerId.Value;
        values["UserEnteredPrice"] = userEnteredPrice;
        if (kitId.HasValue) values["KitId"] = kitId.Value;
        values["IsMPLGItem"] = isMPLGItem;
        values["IsChangedMPLGItem"] = isChangedMPLGItem;
        if (coreCost.HasValue) values["CoreCost"] = (double)coreCost.Value;
        if (coreListPrice.HasValue) values["CoreListPrice"] = (double)coreListPrice.Value;
        if (partFeeId.HasValue) values["PartFeeId"] = partFeeId.Value;
        if (recordVersion.HasValue) values["RecordVersion"] = recordVersion.Value;
        if (!string.IsNullOrWhiteSpace(metadata)) values["Metadata"] = metadata;

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
        var acronyms = new[] { "ID", "PO", "MPLG" };
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

    private static string? GetString(SqlDataReader reader, HashSet<string> columns, string column)
    {
        if (!columns.Contains(column))
        {
            return null;
        }

        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static decimal? GetDecimal(SqlDataReader reader, HashSet<string> columns, string column)
    {
        if (!columns.Contains(column))
        {
            return null;
        }

        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }

    private static int? GetInt(SqlDataReader reader, HashSet<string> columns, string column)
    {
        if (!columns.Contains(column))
        {
            return null;
        }

        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static bool GetBoolean(SqlDataReader reader, HashSet<string> columns, string column)
    {
        if (!columns.Contains(column))
        {
            return false;
        }

        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        object value = reader.GetValue(ordinal);
        return ConvertToBoolean(value);
    }

    private static bool ConvertToBoolean(object? value)
    {
        if (value == null || value == DBNull.Value)
        {
            return false;
        }

        if (value is bool boolValue)
        {
            return boolValue;
        }

        if (value is int intValue)
        {
            return intValue != 0;
        }

        if (value is string stringValue)
        {
            return stringValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   stringValue.Equals("1", StringComparison.OrdinalIgnoreCase);
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
