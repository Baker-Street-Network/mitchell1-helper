using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class OdooRpcClient(
    HttpClient httpClient,
    AppSettings appSettings,
    ILogger<OdooRpcClient> logger)
{
    /// <summary>
    /// Calls an Odoo model method via the JSON-2 API (Odoo 17+).
    /// <para>
    /// The <paramref name="args"/> array maps to named parameters. For <c>search_read</c> pass the domain
    /// as <c>args[0]</c>; kwargs such as <c>fields</c> and <c>limit</c> are forwarded as-is.
    /// </para>
    /// </summary>
    public async Task<JsonElement> ExecuteKwAsync(
        string model,
        string method,
        object[] args,
        Dictionary<string, object>? kwargs,
        CancellationToken cancellationToken)
    {
        // Build named-parameter body expected by JSON-2.
        // Convention: args[0] is the domain/ids positional arg for search-style calls;
        // everything in kwargs goes in at the top level alongside it.
        var body = new Dictionary<string, object?>();

        // Positional args are inlined as named fields where applicable.
        // For write/create the first positional arg carries the record data or ids.
        if (args.Length > 0)
        {
            // search_read, search: first arg is domain
            if (method is "search_read" or "search")
            {
                body["domain"] = args[0];
            }
            // write: args[0] = ids array, args[1] = values dict
            else if (method == "write" && args.Length >= 2)
            {
                body["ids"] = args[0];
                body["vals"] = args[1];
            }
            // create: args[0] = values dict
            else if (method == "create")
            {
                body["vals"] = args[0];
            }
            else
            {
                // Generic fallback: pass as positional "args" list
                body["args"] = args;
            }
        }

        if (kwargs != null)
        {
            foreach (var (key, value) in kwargs)
            {
                body[key] = value;
            }
        }

        using JsonDocument document = await PostJson2Async(model, method, body, cancellationToken);
        return document.RootElement.Clone();
    }

    private async Task<JsonDocument> PostJson2Async(
        string model,
        string method,
        Dictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        string baseUrl = appSettings.Odoo.Url.TrimEnd('/');
        string endpoint = $"{baseUrl}/json/2/{Uri.EscapeDataString(model)}/{Uri.EscapeDataString(method)}";

        string json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        string apiKey = appSettings.Odoo.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", apiKey);
        }

        string database = appSettings.Odoo.Database;
        if (!string.IsNullOrWhiteSpace(database))
        {
            request.Headers.TryAddWithoutValidation("X-Odoo-Database", database);
        }

        logger.LogDebug("Odoo JSON-2 POST {Endpoint}", endpoint);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("error", out JsonElement errorElement))
        {
            string error = errorElement.ToString();
            throw new InvalidOperationException($"Odoo JSON-2 error: {error}");
        }

        return document;
    }
}
