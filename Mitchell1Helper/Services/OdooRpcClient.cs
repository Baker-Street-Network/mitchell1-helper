using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class OdooRpcClient(
    HttpClient httpClient,
    AppSettings appSettings,
    ILogger<OdooRpcClient> logger)
{
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private int? _uid;

    public async Task<JsonElement> ExecuteKwAsync(
        string model,
        string method,
        object[] args,
        Dictionary<string, object>? kwargs,
        CancellationToken cancellationToken)
    {
        int uid = await GetOrAuthenticateAsync(cancellationToken);

        var payload = new
        {
            jsonrpc = "2.0",
            method = "call",
            @params = new
            {
                service = "object",
                method = "execute_kw",
                args = new object[]
                {
                    appSettings.Odoo.Database,
                    uid,
                    appSettings.Odoo.Password,
                    model,
                    method,
                    args,
                    kwargs ?? new Dictionary<string, object>()
                }
            },
            id = Guid.NewGuid().ToString("N")
        };

        using JsonDocument document = await PostJsonRpcAsync(payload, cancellationToken);
        return document.RootElement.GetProperty("result").Clone();
    }

    private async Task<int> GetOrAuthenticateAsync(CancellationToken cancellationToken)
    {
        if (_uid.HasValue)
        {
            return _uid.Value;
        }

        await _authLock.WaitAsync(cancellationToken);
        try
        {
            if (_uid.HasValue)
            {
                return _uid.Value;
            }

            var payload = new
            {
                jsonrpc = "2.0",
                method = "call",
                @params = new
                {
                    service = "common",
                    method = "authenticate",
                    args = new object[]
                    {
                        appSettings.Odoo.Database,
                        appSettings.Odoo.Username,
                        appSettings.Odoo.Password,
                        new Dictionary<string, object>()
                    }
                },
                id = Guid.NewGuid().ToString("N")
            };

            using JsonDocument document = await PostJsonRpcAsync(payload, cancellationToken);
            int uid = document.RootElement.GetProperty("result").GetInt32();
            _uid = uid;
            logger.LogInformation("Authenticated with Odoo as UID {Uid}", uid);
            return uid;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<JsonDocument> PostJsonRpcAsync(object payload, CancellationToken cancellationToken)
    {
        string endpoint = appSettings.Odoo.Url.TrimEnd('/') + "/jsonrpc";
        string json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.TryGetProperty("error", out JsonElement errorElement))
        {
            string error = errorElement.ToString();
            throw new InvalidOperationException($"Odoo JSON-RPC error: {error}");
        }

        return document;
    }
}
