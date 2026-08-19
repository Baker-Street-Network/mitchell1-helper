using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class MitchellCustomerSyncService(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    RecordHashCache hashCache,
    ILogger<MitchellCustomerSyncService> logger)
    : SyncServiceBase(appSettings, odooRpcClient, syncState, hashCache)
{
    private DateTime _lastChangeDateUtc = syncState.LastProcessedChangeDateUtc ?? DateTime.MinValue;
    private long _lastCustomerId = syncState.LastProcessedCustomerId ?? 0;

    private const string CustomerQuery = """
        SELECT TOP (@BatchSize) c.*,
               CONVERT(NVARCHAR(50), c.[LastChangeDate], 127) AS LastChangeDate_String,
               a.[Address]    AS Street,
               a.[City],
               a.[State],
               a.[ZipCode],
               ph.[PhoneNum]  AS Phone,
               mob.[PhoneNum] AS Mobile
        FROM [SM].[Customers] c
        OUTER APPLY (
            SELECT TOP 1 addr.[Address], addr.[City], addr.[State], addr.[ZipCode]
            FROM [SM].[CustomerAddresses] ca
            JOIN [SM].[Addresses] addr ON addr.[AddressId] = ca.[AddressId]
            WHERE ca.[CustID] = c.[CustId]
            ORDER BY addr.[AddressId]
        ) a
        OUTER APPLY (
            SELECT TOP 1 pn.[PhoneNum]
            FROM [SM].[CustomerPhones] cp
            JOIN [SM].[PhoneNum] pn ON pn.[PhoneId] = cp.[PhoneId]
            LEFT JOIN [SM].[PhoneDesc] pd ON pd.[PhoneDescId] = pn.[PhoneDescId]
            WHERE cp.[CustId] = c.[CustId]
              AND ISNULL(pd.[Description], '') NOT IN ('Cellular', 'Fax')
            ORDER BY pn.[Display] DESC, pn.[PhoneId]
        ) ph
        OUTER APPLY (
            SELECT TOP 1 pn.[PhoneNum]
            FROM [SM].[CustomerPhones] cp
            JOIN [SM].[PhoneNum] pn ON pn.[PhoneId] = cp.[PhoneId]
            JOIN [SM].[PhoneDesc] pd ON pd.[PhoneDescId] = pn.[PhoneDescId]
            WHERE cp.[CustId] = c.[CustId]
              AND pd.[Description] = 'Cellular'
            ORDER BY pn.[Display] DESC, pn.[PhoneId]
        ) mob
        WHERE (c.[LastChangeDate] > @LastChangeDate)
           OR (c.[LastChangeDate] = @LastChangeDate AND c.[CustId] > @LastId)
        ORDER BY c.[LastChangeDate] ASC, c.[CustId] ASC;
        """;

    public async Task PollAndSyncAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(AppSettings.Sql.ConnectionString))
        {
            const string msg = "SQL connection string is empty. Update AppData/Mitchell1Helper/settings.json.";
            SyncState.MarkError(msg);
            logger.LogWarning(msg);
            return;
        }

        logger.LogInformation("Starting Customer poll. Last: ChangeDate={Date:O}, Id={Id}",
            _lastChangeDateUtc, _lastCustomerId);

        int processed = 0;
        DateTime? newestChangeDate = null;
        long? newestId = null;

        try
        {
            string cs = EnsureTrustServerCertificate(AppSettings.Sql.ConnectionString);
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = new SqlCommand(CustomerQuery, conn);
            cmd.Parameters.AddWithValue("@BatchSize", AppSettings.Sync.BatchSize);
            cmd.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = _lastChangeDateUtc;
            cmd.Parameters.AddWithValue("@LastId", _lastCustomerId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var (columns, changeDateOrdinal) = ReadColumnSchema(reader);

            while (await reader.ReadAsync(cancellationToken))
            {
                long id = 0;
                try
                {
                    id = Convert.ToInt64(reader[Mitchell1Schema.IdColumn]);
                    DateTime changeDate = ParseChangeDate(reader, changeDateOrdinal, id, "Customer", logger);
                    string domainValue = AppSettings.Odoo.ExternalRefPrefix + id;
                    string cacheKey = $"customer:{domainValue}";

                    bool isDeleted = TryGetBool(reader, columns, Mitchell1Schema.IsDeletedColumn, out bool isDel) && isDel;
                    if (isDeleted)
                    {
                        await ArchiveInOdooAsync(AppSettings.Odoo.CustomerModel, "ref", domainValue, cacheKey, cancellationToken);
                    }
                    else
                    {
                        var values = BuildValues(reader, columns, id, domainValue);
                        await UpsertInOdooAsync(AppSettings.Odoo.CustomerModel, "ref", domainValue, values, cacheKey, cancellationToken);
                    }

                    processed++;
                    newestChangeDate = changeDate;
                    newestId = id;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process Customer {Id}. Skipping.", id);
                }
            }

            if (newestChangeDate.HasValue && newestId.HasValue)
            {
                _lastChangeDateUtc = newestChangeDate.Value;
                _lastCustomerId = newestId.Value;
                SyncState.MarkSuccess(newestChangeDate, newestId, processed);
            }

            logger.LogInformation("Customer poll completed. Processed {Count} change(s)", processed);
        }
        catch (Exception ex)
        {
            SyncState.MarkError(ex.Message);
            logger.LogError(ex, "Error polling Customer changes");
            throw;
        }
    }

    private Dictionary<string, object> BuildValues(SqlDataReader reader, HashSet<string> columns, long id, string domainValue)
    {
        string? firstName = GetString(reader, columns, "FirstName");
        string? lastName = GetString(reader, columns, "LastName");

        var values = new Dictionary<string, object>
        {
            ["ref"] = domainValue,
            ["active"] = true,
            ["mitchell1_customer_id"] = id
        };

        if (!string.IsNullOrWhiteSpace(firstName)) values["first_name"] = firstName;
        if (!string.IsNullOrWhiteSpace(lastName)) values["last_name"] = lastName;

        if (TryGetString(reader, columns, "EmailAddress", out string? email)) values["email"] = email!;
        if (TryGetString(reader, columns, "Street", out string? street)) values["street"] = street!;
        if (TryGetString(reader, columns, "City", out string? city)) values["city"] = city!;
        if (TryGetString(reader, columns, "ZipCode", out string? zip)) values["zip"] = zip!;
        if (TryGetString(reader, columns, "Phone", out string? phone)) values["phone"] = phone!;
        if (TryGetString(reader, columns, "Mobile", out string? mobile)) values["mobile"] = mobile!;

        return values;
    }
}
