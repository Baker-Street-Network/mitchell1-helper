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
            // create: args[0] = values dict (Odoo 17+ requires vals_list: a list of dicts)
            else if (method == "create")
            {
                body["vals_list"] = args[0] is IEnumerable<object> ? args[0] : new object[] { args[0] };
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
        string payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Odoo {model}.{method} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {Describe(payload)}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Odoo {model}.{method} returned a non-JSON response (HTTP {(int)response.StatusCode}). {Describe(payload)}", ex);
        }

        if (TryGetOdooError(document.RootElement, out string? error))
        {
            document.Dispose();
            throw new InvalidOperationException($"Odoo {model}.{method} error: {error}");
        }

        return document;
    }

    /// <summary>
    /// Detects an error payload returned with a 2xx status. Odoo reports these either as a
    /// JSON-RPC style <c>{"error": ...}</c> object or as a serialised werkzeug exception
    /// (<c>{"name": "werkzeug.exceptions.X", "message": ...}</c>), sometimes wrapped in an array.
    /// Without this check an error body is handed back as if it were a successful result.
    /// </summary>
    private static bool TryGetOdooError(JsonElement root, out string? error)
    {
        error = null;

        JsonElement candidate = root;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() != 1) return false;
            candidate = root[0];
        }

        if (candidate.ValueKind != JsonValueKind.Object) return false;

        if (candidate.TryGetProperty("error", out JsonElement errorElement))
        {
            error = errorElement.ToString();
            return true;
        }

        // A real record always carries an "id"; an exception payload carries a dotted
        // exception name plus a message. Requiring all three keeps genuine records
        // (which may legitimately have a "name") from being misread as errors.
        if (!candidate.TryGetProperty("id", out _)
            && candidate.TryGetProperty("name", out JsonElement nameElement)
            && candidate.TryGetProperty("message", out JsonElement messageElement)
            && nameElement.ValueKind == JsonValueKind.String
            && nameElement.GetString() is { } name
            && name.Contains('.'))
        {
            error = $"{name}: {messageElement}";
            return true;
        }

        return false;
    }

    private static string Describe(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return "Response body was empty.";
        string trimmed = payload.Trim();
        const int max = 500;
        return "Response body: " + (trimmed.Length <= max ? trimmed : trimmed[..max] + "...");
    }
}
