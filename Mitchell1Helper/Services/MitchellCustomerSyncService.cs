using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class MitchellCustomerSyncService(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    ILogger<MitchellCustomerSyncService> logger)
{
    private static readonly Regex IdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private DateTime _lastChangeDateUtc = DateTime.MinValue;
    private long _lastCustomerId;

    public async Task PollAndSyncAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appSettings.Sql.ConnectionString))
        {
            const string message = "SQL connection string is empty. Update AppData/Mitchell1Helper/settings.json.";
            syncState.MarkError(message);
            logger.LogWarning(message);
            return;
        }

        int processed = 0;
        DateTime? newestChangeDate = null;
        long? newestCustomerId = null;

        try
        {
            await using var connection = new SqlConnection(appSettings.Sql.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            string table = QuoteIdentifier(appSettings.Sql.CustomersTable);
            string idColumn = QuoteIdentifier(appSettings.Sql.IdColumn);
            string changeDateColumn = QuoteIdentifier(appSettings.Sql.LastChangeDateColumn);
            string isDeletedColumn = QuoteIdentifier(appSettings.Sql.IsDeletedColumn);

            string query = $@"
SELECT TOP (@BatchSize) *
FROM {table}
WHERE ({changeDateColumn} > @LastChangeDate)
   OR ({changeDateColumn} = @LastChangeDate AND {idColumn} > @LastCustomerId)
ORDER BY {changeDateColumn} ASC, {idColumn} ASC;";

            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@BatchSize", appSettings.Sync.BatchSize);
            command.Parameters.AddWithValue("@LastChangeDate", _lastChangeDateUtc);
            command.Parameters.AddWithValue("@LastCustomerId", _lastCustomerId);

            await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in reader.GetColumnSchema())
            {
                if (!string.IsNullOrWhiteSpace(column.ColumnName))
                {
                    columns.Add(column.ColumnName);
                }
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                long customerId = Convert.ToInt64(reader[appSettings.Sql.IdColumn]);
                DateTime changeDate = Convert.ToDateTime(reader[appSettings.Sql.LastChangeDateColumn]);
                bool isDeleted = ConvertToBoolean(reader[appSettings.Sql.IsDeletedColumn]);

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

            if (newestChangeDate.HasValue && newestCustomerId.HasValue)
            {
                _lastChangeDateUtc = newestChangeDate.Value;
                _lastCustomerId = newestCustomerId.Value;
            }

            syncState.MarkSuccess(newestChangeDate, newestCustomerId, processed);
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
        int? existingId = await FindPartnerIdByRefAsync(model, externalRef, cancellationToken);
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
        int? existingId = await FindPartnerIdByRefAsync(model, externalRef, cancellationToken);
        if (!existingId.HasValue)
        {
            return;
        }

        var values = new Dictionary<string, object> { ["active"] = false };
        await odooRpcClient.ExecuteKwAsync(
            model,
            "write",
            new object[] { new[] { existingId.Value }, values },
            null,
            cancellationToken);
    }

    private async Task<int?> FindPartnerIdByRefAsync(string model, string externalRef, CancellationToken cancellationToken)
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

        var idProperty = result[0].GetProperty("id");
        return idProperty.GetInt32();
    }

    private static Dictionary<string, object> BuildPartnerValues(
        SqlDataReader reader,
        HashSet<string> columns,
        long customerId,
        string externalRef)
    {
        string? firstName = GetString(reader, columns, "FirstName");
        string? lastName = GetString(reader, columns, "LastName");
        string? fullName = GetString(reader, columns, "Name");
        string? email = GetString(reader, columns, "Email");
        string? phone = GetString(reader, columns, "Phone");
        string? mobile = GetString(reader, columns, "Mobile");

        string fallbackName = $"Mitchell1 Customer {customerId}";
        string composedName = string.Join(" ", new[] { firstName, lastName }.Where(v => !string.IsNullOrWhiteSpace(v))).Trim();
        string name = FirstNonEmpty(fullName, composedName, fallbackName);

        var values = new Dictionary<string, object>
        {
            ["name"] = name,
            ["ref"] = externalRef,
            ["active"] = true
        };

        if (!string.IsNullOrWhiteSpace(email)) values["email"] = email;
        if (!string.IsNullOrWhiteSpace(phone)) values["phone"] = phone;
        if (!string.IsNullOrWhiteSpace(mobile)) values["mobile"] = mobile;

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

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
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
        if (!IdentifierRegex.IsMatch(identifier))
        {
            throw new InvalidOperationException($"Invalid SQL identifier: {identifier}");
        }

        return $"[{identifier}]";
    }
}
