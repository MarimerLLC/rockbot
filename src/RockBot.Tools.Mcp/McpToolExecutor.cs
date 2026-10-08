using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Delegate that invokes an MCP tool and returns the result.
/// </summary>
internal delegate ValueTask<CallToolResult> CallToolDelegate(
    IReadOnlyDictionary<string, object?>? arguments,
    CancellationToken ct);

/// <summary>
/// Executes a tool invocation by calling an MCP server tool.
/// </summary>
internal sealed class McpToolExecutor(CallToolDelegate callTool) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var arguments = ParseArguments(request.Arguments);

        var result = await callTool(arguments, ct);

        var blocks = MapContentBlocks(result);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            ContentBlocks = blocks,
            Content = blocks is not null ? TextFromBlocks(blocks) : null,
            IsError = result.IsError == true
        };
    }

    internal static Dictionary<string, object?> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        if (raw is null)
            return [];

        return raw.ToDictionary(kvp => kvp.Key, kvp => ConvertJsonElement(kvp.Value));
    }

    internal static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJsonElement)
                .ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.GetRawText(),
        };
    }

    /// <summary>
    /// Maps all MCP content blocks to transport-agnostic <see cref="ToolContentBlock"/> records,
    /// preserving non-text content (images, audio, etc.) rather than silently discarding it.
    /// Resource blocks (#617) become readable text, so they stay in the result's text (and in
    /// working-memory chunking) instead of switching it to the multimodal path; a resource link
    /// names <paramref name="serverName"/>, when known, so the model can follow it with
    /// <c>mcp_read_resource</c>.
    /// </summary>
    internal static IReadOnlyList<ToolContentBlock>? MapContentBlocks(CallToolResult result, string? serverName = null)
    {
        if (result.Content is null || result.Content.Count == 0)
            return null;

        var blocks = new List<ToolContentBlock>(result.Content.Count);
        foreach (var block in result.Content)
        {
            blocks.Add(block switch
            {
                TextContentBlock text => new ToolContentBlock { Type = "text", Text = text.Text },
                ImageContentBlock img => new ToolContentBlock { Type = "image", Data = McpBinaryPayload.ToBase64(img.Data), MimeType = img.MimeType },
                AudioContentBlock audio => new ToolContentBlock { Type = "audio", Data = McpBinaryPayload.ToBase64(audio.Data), MimeType = audio.MimeType },
                EmbeddedResourceBlock embedded => MapEmbeddedResource(embedded.Resource),
                ResourceLinkBlock link => MapResourceLink(link, serverName),
                _ => new ToolContentBlock { Type = block.Type ?? "unknown", Text = $"[{block.Type ?? "unknown"} content block]" }
            });
        }
        return blocks;
    }

    private static ToolContentBlock MapEmbeddedResource(ResourceContents resource) => resource switch
    {
        TextResourceContents text => new ToolContentBlock
        {
            Type = "text",
            Uri = text.Uri,
            MimeType = text.MimeType,
            Text = $"[resource {text.Uri}{MimeSuffix(text.MimeType)}]\n{text.Text}"
        },
        // Binary capture saves these to the shared volume first on the bridge path; one reaching
        // here (an error result, capture turned off) is described, never inlined as base64.
        BlobResourceContents blob => new ToolContentBlock
        {
            Type = "text",
            Uri = blob.Uri,
            MimeType = blob.MimeType,
            Text = $"[binary resource {blob.Uri}{MimeSuffix(blob.MimeType)}, " +
                   $"{McpBinaryPayload.Decode(blob.Blob).LongLength.ToString("N0", CultureInfo.InvariantCulture)} bytes, not shown]"
        },
        _ => new ToolContentBlock { Type = "text", Uri = resource.Uri, Text = $"[resource {resource.Uri}, not shown]" }
    };

    private static ToolContentBlock MapResourceLink(ResourceLinkBlock link, string? serverName)
    {
        var label = !string.IsNullOrWhiteSpace(link.Title) ? link.Title : link.Name;
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(link.MimeType)) details.Add(link.MimeType);
        if (link.Size is long size) details.Add($"{size.ToString("N0", CultureInfo.InvariantCulture)} bytes");
        var detailText = details.Count > 0 ? $" ({string.Join(", ", details)})" : "";
        var follow = serverName is null
            ? ""
            : $" Read it with mcp_read_resource(server_name: \"{serverName}\", uri: \"{link.Uri}\").";

        return new ToolContentBlock
        {
            Type = "text",
            Uri = link.Uri,
            MimeType = link.MimeType,
            Text = $"[resource link] {label}: {link.Uri}{detailText}.{follow}"
        };
    }

    private static string MimeSuffix(string? mime) => string.IsNullOrWhiteSpace(mime) ? "" : $" ({mime})";

    /// <summary>
    /// Extracts and joins text from a list of content blocks. Returns null if there is no text.
    /// </summary>
    internal static string? TextFromBlocks(IReadOnlyList<ToolContentBlock> blocks)
    {
        var joined = string.Join("\n", blocks
            .Where(b => b.Type == "text")
            .Select(b => b.Text ?? "")
            .Where(t => t.Length > 0));
        return string.IsNullOrEmpty(joined) ? null : joined;
    }
}
