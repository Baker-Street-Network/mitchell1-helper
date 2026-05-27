using System.Data;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class OdooCustomerWebSocketListenerService(
    AppSettings appSettings,
    OdooRpcClient odooRpcClient,
    SyncState syncState,
    ILogger<OdooCustomerWebSocketListenerService> logger) : BackgroundService
{
    private static readonly Regex IdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private long _lastMessageId;

    private static string EnsureTrustServerCertificate(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            TrustServerCertificate = true
        };
        return builder.ConnectionString;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Odoo websocket listener started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!CanStart())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                using var webSocket = new ClientWebSocket();
                webSocket.Options.SetRequestHeader("Origin", "127.0.0.1");
                webSocket.Options.RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;

                Uri uri = BuildWebSocketUri(appSettings.Odoo.Url);
                logger.LogInformation("Connecting websocket to {Uri}", uri);
                await webSocket.ConnectAsync(uri, stoppingToken);

                await SendSubscribeMessageAsync(webSocket, stoppingToken);
                await ReceiveLoopAsync(webSocket, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                syncState.MarkError(ex.Message);
                logger.LogWarning(ex, "Websocket disconnected. Retrying...");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (Exception ex)
            {
                syncState.MarkError(ex.Message);
                logger.LogError(ex, "Unexpected websocket listener error. Retrying...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        logger.LogInformation("Odoo websocket listener stopped");
    }

    private bool CanStart()
    {
        if (string.IsNullOrWhiteSpace(appSettings.Odoo.Url))
        {
            logger.LogDebug("Odoo URL is missing. Waiting for configuration.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(appSettings.Sql.ConnectionString))
        {
            logger.LogDebug("SQL connection string missing. Waiting for configuration.");
            return false;
        }

        return true;
    }

    private async Task SendSubscribeMessageAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        string channel = string.IsNullOrWhiteSpace(appSettings.Odoo.WebsocketChannel)
            ? "record_updated"
            : appSettings.Odoo.WebsocketChannel;

        var channels = new List<string>();
        if (!string.IsNullOrWhiteSpace(appSettings.Odoo.WebsocketSecret))
        {
            channels.Add(appSettings.Odoo.WebsocketSecret);
        }
        channels.Add(channel);

        var subscribePayload = new
        {
            event_name = "subscribe",
            data = new
            {
                channels = channels.ToArray(),
                last = _lastMessageId
            }
        };

        string json = JsonSerializer.Serialize(subscribePayload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        logger.LogInformation("Subscribed to Odoo websocket channel {Channel}", channel);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024 * 8];

        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var chunk = new ArraySegment<byte>(buffer);
            using var messageBuffer = new MemoryStream();

            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(chunk, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                    return;
                }

                messageBuffer.Write(chunk.Array!, chunk.Offset, result.Count);
            }
            while (!result.EndOfMessage);

            string json = Encoding.UTF8.GetString(messageBuffer.ToArray());
            await HandleNotificationBatchAsync(json, cancellationToken);
        }
    }

    private async Task HandleNotificationBatchAsync(string json, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement notification in document.RootElement.EnumerateArray())
        {
            if (notification.TryGetProperty("id", out JsonElement idElement) &&
                idElement.ValueKind == JsonValueKind.Number &&
                idElement.TryGetInt64(out long messageId))
            {
                _lastMessageId = messageId;
            }

            if (!notification.TryGetProperty("message", out JsonElement messageElement) ||
                !messageElement.TryGetProperty("payload", out JsonElement payloadElement))
            {
                continue;
            }

            foreach (int customerId in ExtractCustomerIds(payloadElement))
            {
                await SyncCustomerFromOdooToSqlAsync(customerId, cancellationToken);
            }
        }
    }

    private IEnumerable<int> ExtractCustomerIds(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        string model = payload.TryGetProperty("model", out JsonElement modelElement)
            ? modelElement.GetString() ?? string.Empty
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(model) && !model.Equals(appSettings.Odoo.CustomerModel, StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        if (payload.TryGetProperty("customer_id", out JsonElement customerIdElement) &&
            TryReadInt(customerIdElement, out int customerId))
        {
            yield return customerId;
        }

        if (payload.TryGetProperty("id", out JsonElement idElement) && TryReadInt(idElement, out int idValue))
        {
            yield return idValue;
        }

        if (payload.TryGetProperty("ids", out JsonElement idsElement) && idsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in idsElement.EnumerateArray())
            {
                if (TryReadInt(item, out int itemId))
                {
                    yield return itemId;
                }
            }
        }
    }

    private async Task SyncCustomerFromOdooToSqlAsync(int odooCustomerId, CancellationToken cancellationToken)
    {
        var kwargs = new Dictionary<string, object>
        {
            ["fields"] = new[] { "id", "first_name", "last_name", "email", "phone", "active", "ref", "write_date" },
            ["limit"] = 1
        };

        object[] domain = { new object[] { "id", "=", odooCustomerId } };
        JsonElement rows = await odooRpcClient.ExecuteKwAsync(
            appSettings.Odoo.CustomerModel,
            "search_read",
            new object[] { domain },
            kwargs,
            cancellationToken);

        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
        {
            logger.LogWarning("Odoo customer {CustomerId} not found", odooCustomerId);
            return;
        }

        JsonElement row = rows[0];
        if (!TryResolveSqlCustomerId(row, out long sqlCustomerId))
        {
            logger.LogWarning(
                "Skipping Odoo customer {CustomerId}; could not map ref to SQL customer ID with prefix {Prefix}",
                odooCustomerId,
                appSettings.Odoo.ExternalRefPrefix);
            return;
        }

        string firstName = GetString(row, "first_name") ?? string.Empty;
        string lastName = GetString(row, "last_name") ?? string.Empty;
        bool active = GetBool(row, "active", true);
        string rawWriteDate = GetString(row, "write_date") ?? string.Empty;
        DateTime changeDateUtc = ParseWriteDateUtc(rawWriteDate);

        logger.LogInformation(
            "Processing Odoo customer {OdooId} -> SQL {SqlId}: FirstName={FirstName}, LastName={LastName}, RawWriteDate='{RawDate}', ParsedDate={ParsedDate}",
            odooCustomerId, sqlCustomerId, firstName, lastName, rawWriteDate, changeDateUtc);

        await UpsertSqlCustomerAsync(sqlCustomerId, firstName, lastName, !active, changeDateUtc, cancellationToken);

        syncState.MarkSuccess(changeDateUtc, sqlCustomerId, 1);
        logger.LogInformation("Synced Odoo customer {OdooCustomerId} to SQL customer {SqlCustomerId}", odooCustomerId, sqlCustomerId);
    }

    private async Task UpsertSqlCustomerAsync(
        long customerId,
        string firstName,
        string lastName,
        bool isDeleted,
        DateTime lastChangeDateUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Mitchell1Schema.IsValidSqlDateTime(lastChangeDateUtc))
            {
                logger.LogWarning(
                    "Invalid date {Date} for customer {CustomerId}. Clamping to valid SQL Server range.",
                    lastChangeDateUtc, customerId);
                lastChangeDateUtc = Mitchell1Schema.ClampToValidRange(lastChangeDateUtc);
            }

            string connectionString = EnsureTrustServerCertificate(appSettings.Sql.ConnectionString);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            string table = QuoteIdentifier(Mitchell1Schema.CustomersTable);
            string idColumn = QuoteIdentifier(Mitchell1Schema.IdColumn);
            string lastChangeColumn = QuoteIdentifier(Mitchell1Schema.LastChangeDateColumn);
            string isDeletedColumn = QuoteIdentifier(Mitchell1Schema.IsDeletedColumn);

            string updateSql = $@"
UPDATE {table}
SET [FirstName] = @FirstName,
    [LastName] = @LastName,
    {isDeletedColumn} = @IsDeleted,
    {lastChangeColumn} = @LastChangeDate
WHERE {idColumn} = @CustomerId;";

            await using var updateCommand = new SqlCommand(updateSql, connection);
            updateCommand.Parameters.AddWithValue("@FirstName", firstName);
            updateCommand.Parameters.AddWithValue("@LastName", lastName);
            updateCommand.Parameters.AddWithValue("@IsDeleted", isDeleted);
            updateCommand.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = lastChangeDateUtc;
            updateCommand.Parameters.AddWithValue("@CustomerId", customerId);

            int affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affected > 0)
            {
                return;
            }

            string insertSql = $@"
INSERT INTO {table} ({idColumn}, [FirstName], [LastName], {isDeletedColumn}, {lastChangeColumn})
VALUES (@CustomerId, @FirstName, @LastName, @IsDeleted, @LastChangeDate);";

            await using var insertCommand = new SqlCommand(insertSql, connection);
            insertCommand.Parameters.AddWithValue("@CustomerId", customerId);
            insertCommand.Parameters.AddWithValue("@FirstName", firstName);
            insertCommand.Parameters.AddWithValue("@LastName", lastName);
            insertCommand.Parameters.AddWithValue("@IsDeleted", isDeleted);
            insertCommand.Parameters.Add("@LastChangeDate", SqlDbType.DateTime2).Value = lastChangeDateUtc;
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to upsert customer. CustomerId={CustomerId}, FirstName={FirstName}, LastName={LastName}, IsDeleted={IsDeleted}, LastChangeDate={LastChangeDate}",
                customerId, firstName, lastName, isDeleted, lastChangeDateUtc);
            throw;
        }
    }

    private bool TryResolveSqlCustomerId(JsonElement row, out long sqlCustomerId)
    {
        sqlCustomerId = 0;

        string? externalRef = GetString(row, "ref");
        if (string.IsNullOrWhiteSpace(externalRef))
        {
            return false;
        }

        if (!externalRef.StartsWith(appSettings.Odoo.ExternalRefPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rawId = externalRef.Substring(appSettings.Odoo.ExternalRefPrefix.Length);
        return long.TryParse(rawId, out sqlCustomerId);
    }

    private static DateTime ParseWriteDateUtc(string? value)
    {
        if (DateTime.TryParse(value, out DateTime parsed))
        {
            return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
        }

        return DateTime.UtcNow;
    }

    private static string? GetString(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out JsonElement element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool GetBool(JsonElement obj, string property, bool fallback)
    {
        if (!obj.TryGetProperty(property, out JsonElement element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out bool parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int intValue))
        {
            value = intValue;
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out int parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static Uri BuildWebSocketUri(string baseUrl)
    {
        string wsBase = baseUrl
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        return new Uri($"{wsBase}/websocket");
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
