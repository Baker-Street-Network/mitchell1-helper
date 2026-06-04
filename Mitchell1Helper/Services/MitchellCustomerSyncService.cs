using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class MitchellCustomerSyncService(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    RecordHashCache hashCache,
    ILogger<MitchellCustomerSyncService> logger)
{
    private static readonly Regex IdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private DateTime _lastChangeDateUtc = syncState.LastProcessedChangeDateUtc ?? DateTime.MinValue;
    private long _lastCustomerId = syncState.LastProcessedCustomerId ?? 0;

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
            syncState.MarkError(message);
            logger.LogWarning(message);
            return;
        }

        logger.LogInformation("Starting poll. Last processed: ChangeDate={LastChangeDate:O}, CustomerId={LastCustomerId}",
            _lastChangeDateUtc, _lastCustomerId);

        int processed = 0;
        DateTime? newestChangeDate = null;
        long? newestCustomerId = null;

        try
        {
            string connectionString = EnsureTrustServerCertificate(appSettings.Sql.ConnectionString);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            string table = QuoteIdentifier(Mitchell1Schema.CustomersTable);
            string idColumn = QuoteIdentifier(Mitchell1Schema.IdColumn);
            string changeDateColumn = QuoteIdentifier(Mitchell1Schema.LastChangeDateColumn);
            string isDeletedColumn = QuoteIdentifier(Mitchell1Schema.IsDeletedColumn);

            // Select all columns plus a string version of LastChangeDate to avoid SqlDateTime overflow
            // datetime2 can contain dates before 1753, which SqlDateTime cannot handle
            string query = $@"
SELECT TOP (@BatchSize) *, 
       CONVERT(NVARCHAR(50), {changeDateColumn}, 127) AS LastChangeDate_String
FROM {table}
WHERE ({changeDateColumn} > @LastChangeDate)
   OR ({changeDateColumn} = @LastChangeDate AND {idColumn} > @LastCustomerId)
ORDER BY {changeDateColumn} ASC, {idColumn} ASC;";

            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@BatchSize", appSettings.Sync.BatchSize);
            // Must use DateTime2 explicitly — AddWithValue infers `datetime` which overflows for DateTime.MinValue (year 0001)
            command.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = _lastChangeDateUtc;
            command.Parameters.AddWithValue("@LastCustomerId", _lastCustomerId);

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
                long customerId = 0;
                try
                {
                    customerId = Convert.ToInt64(reader[Mitchell1Schema.IdColumn]);

                    DateTime changeDate;
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
                            logger.LogWarning("Customer {CustomerId}: LastChangeDate_String column not found in result set!", customerId);
                        }

                        if (string.IsNullOrWhiteSpace(dateString))
                        {
                            logger.LogWarning("NULL or empty LastChangeDate for customer {CustomerId}. Using current UTC time.", customerId);
                            changeDate = DateTime.UtcNow;
                        }
                        else if (DateTime.TryParse(dateString, out DateTime parsed))
                        {
                            changeDate = parsed.Kind == DateTimeKind.Utc ? parsed : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

                            if (!Mitchell1Schema.IsValidSqlDateTime(changeDate))
                            {
                                logger.LogWarning(
                                    "Invalid date {Date} (from string: '{DateString}') for customer {CustomerId}. Clamping to valid range.",
                                    changeDate, dateString, customerId);
                                changeDate = Mitchell1Schema.ClampToValidRange(changeDate);
                            }
                        }
                        else
                        {
                            logger.LogWarning(
                                "Could not parse LastChangeDate string '{DateString}' for customer {CustomerId}. Using current UTC time.",
                                dateString, customerId);
                            changeDate = DateTime.UtcNow;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Failed to read LastChangeDate for customer {CustomerId}. Using current UTC time instead.",
                            customerId);
                        changeDate = DateTime.UtcNow;
                    }

                    bool isDeleted = ConvertToBoolean(reader[Mitchell1Schema.IsDeletedColumn]);

                    string externalRef = appSettings.Odoo.ExternalRefPrefix + customerId;

                    if (isDeleted)
                    {
                        await ArchiveCustomerInOdooAsync(appSettings.Odoo.CustomerModel, externalRef, cancellationToken);
                    }
                    else
                    {
                        Dictionary<string, object> values = BuildPartnerValues(reader, columns, customerId, externalRef);
                        await UpsertCustomerInOdooAsync(appSettings.Odoo.CustomerModel, externalRef, values, cancellationToken);
                    }

                    processed++;
                    newestChangeDate = changeDate;
                    newestCustomerId = customerId;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to process customer {CustomerId}. Skipping this record and continuing.",
                        customerId);
                }
            }

            if (newestChangeDate.HasValue && newestCustomerId.HasValue)
            {
                _lastChangeDateUtc = newestChangeDate.Value;
                _lastCustomerId = newestCustomerId.Value;
                syncState.MarkSuccess(newestChangeDate, newestCustomerId, processed);
            }

            logger.LogInformation("Polling completed. Processed {Count} customer change(s)", processed);
        }
        catch (Exception ex)
        {
            syncState.MarkError(ex.Message);
            logger.LogError(ex, "Error polling Mitchell1 Customers changes");
            throw;
        }
    }

    private async Task UpsertCustomerInOdooAsync(
        string model,
        string externalRef,
        Dictionary<string, object> values,
        CancellationToken cancellationToken)
    {
        string cacheKey = $"customer:{externalRef}";

        if (!hashCache.HasChangedAndUpdate(cacheKey, values))
        {
            return; // Nothing changed since last sync
        }

        var existingId = await FindPartnerByRefAsync(model, externalRef, cancellationToken);
        if (existingId.HasValue)
        {
            await odooRpcClient.ExecuteKwAsync(
                model,
                "write",
                new object[] { new[] { existingId.Value }, values },
                null,
                cancellationToken);
            return;
        }

        await odooRpcClient.ExecuteKwAsync(
            model,
            "create",
            new object[] { values },
            null,
            cancellationToken);
    }

    private async Task ArchiveCustomerInOdooAsync(string model, string externalRef, CancellationToken cancellationToken)
    {
        string cacheKey = $"customer:{externalRef}";
        var archiveValues = new Dictionary<string, object> { ["active"] = false };

        if (!hashCache.HasChangedAndUpdate(cacheKey, archiveValues))
        {
            return;
        }

        var existingId = await FindPartnerByRefAsync(model, externalRef, cancellationToken);
        if (!existingId.HasValue)
        {
            return;
        }

        await odooRpcClient.ExecuteKwAsync(
            model,
            "write",
            new object[] { new[] { existingId.Value }, archiveValues },
            null,
            cancellationToken);
    }

    private async Task<int?> FindPartnerByRefAsync(string model, string externalRef, CancellationToken cancellationToken)
    {
        object[] domain = { new object[] { "ref", "=", externalRef } };
        var kwargs = new Dictionary<string, object>
        {
            ["fields"] = new[] { "id" },
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

        return result[0].GetProperty("id").GetInt32();
    }

    private static Dictionary<string, object> BuildPartnerValues(
        SqlDataReader reader,
        HashSet<string> columns,
        long customerId,
        string externalRef)
    {
        string? firstName = GetString(reader, columns, "FirstName");
        string? lastName = GetString(reader, columns, "LastName");

        var values = new Dictionary<string, object>
        {
            ["ref"] = externalRef,
            ["active"] = true,
            ["mitchell1_customer_id"] = customerId
        };

        if (!string.IsNullOrWhiteSpace(firstName)) values["first_name"] = firstName;
        if (!string.IsNullOrWhiteSpace(lastName)) values["last_name"] = lastName;

        return values;
    }

    private static string? GetString(SqlDataReader reader, HashSet<string> columns, string column)
    {
        if (!columns.Contains(column))
        {
            return null;
        }

        object value = reader[column];
        return value == DBNull.Value ? null : Convert.ToString(value);
    }

    private static bool ConvertToBoolean(object value)
    {
        if (value == DBNull.Value)
        {
            return false;
        }

        if (value is bool boolValue)
        {
            return boolValue;
        }

        if (value is byte byteValue)
        {
            return byteValue != 0;
        }

        if (value is short shortValue)
        {
            return shortValue != 0;
        }

        if (value is int intValue)
        {
            return intValue != 0;
        }

        if (value is long longValue)
        {
            return longValue != 0;
        }

        if (value is string stringValue)
        {
            return stringValue.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   stringValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   stringValue.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                   stringValue.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        return Convert.ToBoolean(value);
    }

    private static string QuoteIdentifier(string identifier)
    {
        // Handle schema-qualified names (e.g., "SM.Customers")
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
