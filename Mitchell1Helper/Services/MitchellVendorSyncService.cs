using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class MitchellVendorSyncService(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    RecordHashCache hashCache,
    ILogger<MitchellVendorSyncService> logger)
    : SyncServiceBase(appSettings, odooRpcClient, syncState, hashCache)
{
    private DateTime _lastChangeDateUtc = syncState.LastProcessedVendorChangeDateUtc ?? DateTime.MinValue;
    private long _lastVendorId = syncState.LastProcessedVendorId ?? 0;

    private const string VendorQuery = """
        SELECT TOP (@BatchSize) v.*,
               CONVERT(NVARCHAR(50), v.[LastChangeDate], 127) AS LastChangeDate_String,
               a.[Address]   AS Street,
               a.[City],
               a.[State],
               a.[ZipCode],
               ph.[PhoneNum] AS Phone
        FROM [SM].[Vendor] v
        OUTER APPLY (
            SELECT TOP 1 addr.[Address], addr.[City], addr.[State], addr.[ZipCode]
            FROM [SM].[VendorAddresses] va
            JOIN [SM].[Addresses] addr ON addr.[AddressId] = va.[AddressId]
            WHERE va.[VendorId] = v.[VendorId]
            ORDER BY addr.[AddressId]
        ) a
        OUTER APPLY (
            SELECT TOP 1 pn.[PhoneNum]
            FROM [SM].[VendorPhones] vp
            JOIN [SM].[PhoneNum] pn ON pn.[PhoneId] = vp.[PhoneId]
            LEFT JOIN [SM].[PhoneDesc] pd ON pd.[PhoneDescId] = pn.[PhoneDescId]
            WHERE vp.[VendorId] = v.[VendorId]
              AND ISNULL(pd.[Description], '') NOT IN ('Cellular', 'Fax')
            ORDER BY pn.[Display] DESC, pn.[PhoneId]
        ) ph
        WHERE (v.[LastChangeDate] > @LastChangeDate)
           OR (v.[LastChangeDate] = @LastChangeDate AND v.[VendorId] > @LastId)
        ORDER BY v.[LastChangeDate] ASC, v.[VendorId] ASC;
        """;

    public async Task PollAndSyncAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(AppSettings.Sql.ConnectionString))
        {
            const string msg = "SQL connection string is empty. Update AppData/Mitchell1Helper/settings.json.";
            SyncState.MarkVendorError(msg);
            logger.LogWarning(msg);
            return;
        }

        logger.LogInformation("Starting Vendor poll. Last: ChangeDate={Date:O}, Id={Id}",
            _lastChangeDateUtc, _lastVendorId);

        int processed = 0;
        DateTime? newestChangeDate = null;
        long? newestId = null;

        try
        {
            string cs = EnsureTrustServerCertificate(AppSettings.Sql.ConnectionString);
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = new SqlCommand(VendorQuery, conn);
            cmd.Parameters.AddWithValue("@BatchSize", AppSettings.Sync.BatchSize);
            cmd.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = _lastChangeDateUtc;
            cmd.Parameters.AddWithValue("@LastId", _lastVendorId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var (columns, changeDateOrdinal) = ReadColumnSchema(reader);

            while (await reader.ReadAsync(cancellationToken))
            {
                long id = 0;
                try
                {
                    id = Convert.ToInt64(reader[Mitchell1Schema.VendorIdColumn]);
                    DateTime changeDate = ParseChangeDate(reader, changeDateOrdinal, id, "Vendor", logger);
                    string domainValue = AppSettings.Odoo.ExternalRefPrefix + "VENDOR-" + id;
                    string cacheKey = $"vendor:{domainValue}";

                    bool isDeleted = TryGetBool(reader, columns, Mitchell1Schema.DeletedColumn, out bool isDel) && isDel;
                    if (isDeleted)
                    {
                        await ArchiveInOdooAsync(AppSettings.Odoo.VendorModel, "ref", domainValue, cacheKey, cancellationToken);
                    }
                    else
                    {
                        var values = BuildValues(reader, columns, id, domainValue);
                        await UpsertInOdooAsync(AppSettings.Odoo.VendorModel, "ref", domainValue, values, cacheKey, cancellationToken);
                    }

                    processed++;
                    newestChangeDate = changeDate;
                    newestId = id;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process Vendor {Id}. Skipping.", id);
                }
            }

            if (newestChangeDate.HasValue && newestId.HasValue)
            {
                _lastChangeDateUtc = newestChangeDate.Value;
                _lastVendorId = newestId.Value;
                SyncState.MarkVendorSuccess(newestChangeDate, newestId, processed);
            }

            logger.LogInformation("Vendor poll completed. Processed {Count} change(s)", processed);
        }
        catch (Exception ex)
        {
            SyncState.MarkVendorError(ex.Message);
            logger.LogError(ex, "Error polling Vendor changes");
            throw;
        }
    }

    private static Dictionary<string, object> BuildValues(SqlDataReader reader, HashSet<string> columns, long id, string domainValue)
    {
        string? name = GetString(reader, columns, "Name");

        var values = new Dictionary<string, object>
        {
            ["ref"] = domainValue,
            ["active"] = true,
            ["mitchell1_vendor_id"] = id,
            ["supplier_rank"] = 1
        };

        if (!string.IsNullOrWhiteSpace(name)) values["name"] = name;

        if (TryGetString(reader, columns, "EmailAddress", out string? email)) values["email"] = email!;
        if (TryGetString(reader, columns, "Street", out string? street)) values["street"] = street!;
        if (TryGetString(reader, columns, "City", out string? city)) values["city"] = city!;
        if (TryGetString(reader, columns, "ZipCode", out string? zip)) values["zip"] = zip!;
        if (TryGetString(reader, columns, "Phone", out string? phone)) values["phone"] = phone!;

        return values;
    }
}
