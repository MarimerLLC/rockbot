using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace RockBot.Agent.McpBridge.Attachments;

/// <summary>
/// Per-server transform that hides binary attachment payloads from the LLM. The model speaks
/// paths into the shared volume; the gateway translates those paths into the inline-base64
/// or stash-handle shapes that MCP servers actually understand, and reverses the process for
/// the gateway-only <c>mode: "save"</c> response shape.
/// </summary>
public sealed class AttachmentGateway
{
    private readonly IAttachmentStorage _storage;
    private readonly HttpClient _httpClient;
    private readonly Uri _serverBaseUrl;
    private readonly AttachmentManifest _manifest;
    private readonly ILogger? _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AttachmentGateway(
        IAttachmentStorage storage,
        HttpClient httpClient,
        Uri serverBaseUrl,
        AttachmentManifest manifest,
        ILogger? logger = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _serverBaseUrl = serverBaseUrl ?? throw new ArgumentNullException(nameof(serverBaseUrl));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the response from <paramref name="toolName"/> will need to be rewritten
    /// by <see cref="RewriteResponseAsync"/>. Inspects the original (pre-rewrite) <c>mode</c>
    /// argument — the gateway-only value <c>"save"</c> is the trigger.
    /// </summary>
    public bool ShouldRewriteResponse(string toolName, IReadOnlyDictionary<string, object?> args)
    {
        if (_manifest.Inbound is null) return false;
        if (!ToolMatches(toolName)) return false;
        return string.Equals(GetStringValue(args, "mode"), "save", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Outbound rewrite. Walks every configured <c>paramPaths</c> entry and replaces each
    /// <c>{path}</c> attachment object with either an inline <c>{name, base64Content}</c>
    /// (below threshold) or a stashed <c>{attachmentId}</c> (at/above threshold). Also, on
    /// inbound-eligible tools, swaps the gateway-only <c>mode: "save"</c> to either
    /// <c>"stash"</c> (default) or <c>"inline"</c> (when a small <c>sizeHint</c> is supplied)
    /// so the underlying server sees a value it understands.
    /// </summary>
    public async Task RewriteRequestAsync(
        string toolName,
        Dictionary<string, object?> args,
        CancellationToken ct)
    {
        if (_manifest.Outbound is { } outbound)
        {
            foreach (var paramPath in outbound.ParamPaths)
            {
                await ApplyOutboundParamPathAsync(args, paramPath, ct);
            }
        }

        if (_manifest.Inbound is not null
            && ToolMatches(toolName)
            && string.Equals(GetStringValue(args, "mode"), "save", StringComparison.OrdinalIgnoreCase))
        {
            args["mode"] = ResolveSaveMode(args);
        }
    }

    /// <summary>
    /// Inbound rewrite. Reads the now-effective <c>mode</c> from <paramref name="args"/>
    /// (set by <see cref="RewriteRequestAsync"/>) and either fetches the stashed bytes via
    /// <c>GET /attachments/{id}</c> + <c>DELETE</c>, or decodes the inlined base64.
    /// In both cases the bytes are written under the shared attachments directory and the
    /// response is rewritten to a single text content block carrying
    /// <c>{path, name, size, mime}</c>.
    /// </summary>
    public async Task<CallToolResult> RewriteResponseAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        CallToolResult result,
        CancellationToken ct)
    {
        if (result.IsError == true)
        {
            // Underlying tool reported an error — leave the message alone for the model.
            return result;
        }

        var effectiveMode = GetStringValue(args, "mode")?.ToLowerInvariant() ?? "stash";
        var (payload, structured) = ExtractResponsePayload(result);
        if (payload is null)
        {
            _logger?.LogWarning(
                "Attachment gateway: no JSON payload found in response for {Tool} (mode={Mode})",
                toolName, effectiveMode);
            return result;
        }

        try
        {
            return effectiveMode switch
            {
                "inline" => await BuildInlineResultAsync(payload.Value, structured, ct),
                _ => await BuildStashResultAsync(payload.Value, structured, ct),
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Attachment gateway: failed to rewrite {Tool} response (mode={Mode})",
                toolName, effectiveMode);
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock
                {
                    Text = $"Attachment gateway failed to materialize the file: {ex.Message}"
                }]
            };
        }
    }

    private string ResolveSaveMode(IReadOnlyDictionary<string, object?> args)
    {
        if (TryGetLongValue(args, "sizeHint", out var hint) && hint < _manifest.ThresholdBytes)
            return "inline";
        return "stash";
    }

    private bool ToolMatches(string toolName) =>
        _manifest.Inbound is { } inbound &&
        inbound.Tools.Any(t => string.Equals(t, toolName, StringComparison.OrdinalIgnoreCase));

    // ── Outbound (request) rewrite ────────────────────────────────────────────

    private async Task ApplyOutboundParamPathAsync(
        Dictionary<string, object?> args,
        string paramPath,
        CancellationToken ct)
    {
        // First version: support exactly the `arrayKey[*]` shape — an array of attachment objects.
        var iteratorIdx = paramPath.IndexOf("[*]", StringComparison.Ordinal);
        if (iteratorIdx <= 0) return;

        var arrayKey = paramPath[..iteratorIdx];
        if (!args.TryGetValue(arrayKey, out var arrayValue) || arrayValue is null) return;

        var items = NormalizeToList(arrayValue);
        if (items is null) return;

        for (var i = 0; i < items.Count; i++)
        {
            var item = NormalizeToDict(items[i]);
            if (item is null) continue;

            var path = GetStringValue(item, "path");
            if (string.IsNullOrEmpty(path)) continue;

            var bytes = await _storage.ReadAsync(path, ct);
            var name = Path.GetFileName(path);

            item.Remove("path");
            if (bytes.LongLength < _manifest.ThresholdBytes)
            {
                item["name"] = name;
                item["base64Content"] = Convert.ToBase64String(bytes);
            }
            else
            {
                var attachmentId = await UploadAttachmentAsync(name, bytes, ct);
                item["attachmentId"] = attachmentId;
            }

            items[i] = item;
        }

        args[arrayKey] = items;
    }

    private async Task<string> UploadAttachmentAsync(string name, byte[] data, CancellationToken ct)
    {
        var endpoint = BuildEndpointUri();
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMime(name));
        content.Add(fileContent, _manifest.UploadFieldName, name);

        using var response = await _httpClient.PostAsync(endpoint, content, ct);
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Created)
        {
            var body = await SafeReadBodyAsync(response, ct);
            throw new HttpRequestException(
                $"POST {endpoint} returned {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(responseBody))
            throw new InvalidOperationException($"POST {endpoint} returned empty body — expected JSON with attachmentId.");

        using var doc = JsonDocument.Parse(responseBody);
        var id = ReadAttachmentId(doc.RootElement);
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException(
                $"POST {endpoint} response did not include an attachmentId field. Body: {responseBody}");
        return id;
    }

    // ── Inbound (response) rewrite ────────────────────────────────────────────

    private async Task<CallToolResult> BuildStashResultAsync(
        JsonElement payload,
        bool structured,
        CancellationToken ct)
    {
        var attachmentId = ReadAttachmentId(payload)
            ?? throw new InvalidOperationException("Stashed response missing attachmentId.");
        var name = ReadStringField(payload, "name") ?? attachmentId;
        var mime = ReadStringField(payload, "mime")
            ?? ReadStringField(payload, "mimeType")
            ?? ReadStringField(payload, "contentType");

        var (bytes, contentDispositionName, contentType) = await DownloadAttachmentAsync(attachmentId, ct);
        if (!string.IsNullOrEmpty(contentDispositionName)) name = contentDispositionName;
        mime ??= contentType;

        var fullPath = await _storage.WriteAsync(name, bytes, ct);

        // Fire-and-forget delete; we don't block the agent on cleanup.
        _ = TryDeleteAttachmentAsync(attachmentId);

        return BuildPathResult(fullPath, bytes.LongLength, mime, structured);
    }

    private async Task<CallToolResult> BuildInlineResultAsync(
        JsonElement payload,
        bool structured,
        CancellationToken ct)
    {
        var base64 = ReadStringField(payload, "base64Content")
            ?? ReadStringField(payload, "base64")
            ?? ReadStringField(payload, "data")
            ?? throw new InvalidOperationException("Inline response missing base64Content.");
        var name = ReadStringField(payload, "name") ?? "attachment";
        var mime = ReadStringField(payload, "mime")
            ?? ReadStringField(payload, "mimeType")
            ?? ReadStringField(payload, "contentType");

        var bytes = Convert.FromBase64String(base64);
        var fullPath = await _storage.WriteAsync(name, bytes, ct);

        return BuildPathResult(fullPath, bytes.LongLength, mime, structured);
    }

    private static CallToolResult BuildPathResult(string fullPath, long size, string? mime, bool _)
    {
        var name = Path.GetFileName(fullPath);
        var json = JsonSerializer.Serialize(new
        {
            path = fullPath,
            name,
            size,
            mime
        }, JsonOptions);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }]
        };
    }

    private async Task<(byte[] bytes, string? fileName, string? contentType)> DownloadAttachmentAsync(
        string attachmentId,
        CancellationToken ct)
    {
        var endpoint = BuildEndpointUri(attachmentId);
        using var response = await _httpClient.GetAsync(endpoint, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        var contentType = response.Content.Headers.ContentType?.MediaType;
        return (bytes, fileName, contentType);
    }

    private async Task TryDeleteAttachmentAsync(string attachmentId)
    {
        try
        {
            var endpoint = BuildEndpointUri(attachmentId);
            using var response = await _httpClient.DeleteAsync(endpoint);
            if (response.StatusCode != HttpStatusCode.NoContent
                && response.StatusCode != HttpStatusCode.OK
                && response.StatusCode != HttpStatusCode.NotFound)
            {
                _logger?.LogDebug(
                    "Attachment cleanup DELETE {Endpoint} returned unexpected status {Status}",
                    endpoint, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Attachment cleanup DELETE failed for {AttachmentId}", attachmentId);
        }
    }

    private Uri BuildEndpointUri(string? id = null)
    {
        var path = _manifest.EndpointPath;
        if (string.IsNullOrEmpty(path))
            path = "/attachments";
        if (!path.StartsWith('/')) path = "/" + path;
        if (!string.IsNullOrEmpty(id))
            path = path.TrimEnd('/') + "/" + Uri.EscapeDataString(id);

        // Build off the server's authority — the URL stored in mcp.json typically points at
        // a transport-specific endpoint (e.g. ".../sse"), but the attachment REST endpoints
        // live at the server root. Operators who need a non-default prefix can express it
        // via EndpointPath (e.g. "/api/attachments").
        var builder = new UriBuilder
        {
            Scheme = _serverBaseUrl.Scheme,
            Host = _serverBaseUrl.Host,
            Port = _serverBaseUrl.IsDefaultPort ? -1 : _serverBaseUrl.Port,
            Path = path
        };
        return builder.Uri;
    }

    // ── Payload extraction helpers ────────────────────────────────────────────

    private static (JsonElement? payload, bool structured) ExtractResponsePayload(CallToolResult result)
    {
        if (result.Content is null) return (null, false);

        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text && !string.IsNullOrWhiteSpace(text.Text))
            {
                if (TryParseJson(text.Text, out var element))
                    return (element, false);
            }
        }
        return (null, false);
    }

    private static bool TryParseJson(string text, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static string? ReadStringField(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (payload.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        // Try camelCase ↔ original variations
        foreach (var prop2 in payload.EnumerateObject())
        {
            if (string.Equals(prop2.Name, name, StringComparison.OrdinalIgnoreCase)
                && prop2.Value.ValueKind == JsonValueKind.String)
                return prop2.Value.GetString();
        }
        return null;
    }

    private static string? ReadAttachmentId(JsonElement payload)
    {
        return ReadStringField(payload, "attachmentId")
            ?? ReadStringField(payload, "id");
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    // ── JSON / dictionary normalization ───────────────────────────────────────

    private static List<object?>? NormalizeToList(object? value)
    {
        switch (value)
        {
            case List<object?> list:
                return list;
            case IEnumerable<object?> enumerable:
                return enumerable.ToList();
            case JsonElement je when je.ValueKind == JsonValueKind.Array:
                return je.EnumerateArray().Select(JsonElementToObject).ToList();
            default:
                return null;
        }
    }

    private static Dictionary<string, object?>? NormalizeToDict(object? value)
    {
        switch (value)
        {
            case Dictionary<string, object?> dict:
                return dict;
            case IDictionary<string, object?> idict:
                return new Dictionary<string, object?>(idict, StringComparer.OrdinalIgnoreCase);
            case JsonElement je when je.ValueKind == JsonValueKind.Object:
                return je.EnumerateObject()
                    .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value), StringComparer.OrdinalIgnoreCase);
            default:
                return null;
        }
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            _ => element.GetRawText(),
        };
    }

    private static string? GetStringValue(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val)) return null;
        return val switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            JsonElement je => je.GetRawText(),
            null => null,
            _ => val.ToString()
        };
    }

    private static bool TryGetLongValue(IReadOnlyDictionary<string, object?> args, string key, out long value)
    {
        value = 0;
        if (!args.TryGetValue(key, out var val) || val is null) return false;
        switch (val)
        {
            case long l:
                value = l;
                return true;
            case int i:
                value = i;
                return true;
            case double d:
                value = (long)d;
                return true;
            case JsonElement je when je.ValueKind == JsonValueKind.Number:
                if (je.TryGetInt64(out var jl)) { value = jl; return true; }
                if (je.TryGetDouble(out var jd)) { value = (long)jd; return true; }
                return false;
            case string s when long.TryParse(s, out var sl):
                value = sl;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Content type for an uploaded file. Shared with binary capture so the two halves of
    /// the gateway cannot disagree about what a <c>.png</c> is.
    /// </summary>
    private static string GuessMime(string fileName) => AttachmentMime.FromFileName(fileName);
}
