using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class OdooHeartbeatWebSocketService(
    AppSettings appSettings,
    SyncState syncState,
    ILogger<OdooHeartbeatWebSocketService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Odoo heartbeat websocket service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(appSettings.Odoo.Url))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                using var webSocket = new ClientWebSocket();
                webSocket.Options.SetRequestHeader("Origin", "127.0.0.1");
                webSocket.Options.RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;

                Uri uri = BuildWebSocketUri(appSettings.Odoo.Url);
                logger.LogInformation("Connecting heartbeat websocket to {Uri}", uri);
                await webSocket.ConnectAsync(uri, stoppingToken);

                await SendHeartbeatAsync(webSocket, stoppingToken);

                int intervalSeconds = Math.Max(5, appSettings.Sync.HeartbeatIntervalSeconds);
                while (webSocket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
                    await SendHeartbeatAsync(webSocket, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                logger.LogWarning(ex, "Heartbeat websocket disconnected. Retrying...");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Heartbeat websocket error. Retrying...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        logger.LogInformation("Odoo heartbeat websocket service stopped");
    }

    private async Task SendHeartbeatAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        var data = new Dictionary<string, object?>
        {
            ["channel"] = "mitchell1_status",
            ["app"] = "Mitchell1Helper",
            ["version"] = version,
            ["machine_name"] = Environment.MachineName,
            ["status"] = "running",
            ["utc_now"] = DateTime.UtcNow.ToString("O"),
            ["last_successful_poll_utc"] = syncState.LastSuccessfulPollUtc?.ToString("O"),
            ["last_error"] = syncState.LastError
        };

        if (!string.IsNullOrWhiteSpace(appSettings.Odoo.WebsocketSecret))
        {
            data["secret"] = appSettings.Odoo.WebsocketSecret;
        }

        var payload = new
        {
            event_name = "heartbeat",
            data
        };

        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        syncState.MarkHeartbeatSent();
        logger.LogDebug("Heartbeat sent to Odoo websocket");
    }

    private static Uri BuildWebSocketUri(string baseUrl)
    {
        string wsBase = baseUrl
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        return new Uri($"{wsBase}/websocket");
    }
}
