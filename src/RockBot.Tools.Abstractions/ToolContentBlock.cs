namespace RockBot.Tools;

/// <summary>
/// Transport-agnostic representation of a single content block in a tool result.
/// Mirrors the MCP protocol content block model without a dependency on the MCP SDK.
/// </summary>
public sealed record ToolContentBlock
{
    /// <summary>Block type: "text", "image", "audio", or "resource".</summary>
    public required string Type { get; init; }

    /// <summary>Text content (type "text").</summary>
    public string? Text { get; init; }

    /// <summary>Base64-encoded binary data (types "image" or "audio").</summary>
    public string? Data { get; init; }

    /// <summary>MIME type for binary content (types "image" or "audio").</summary>
    public string? MimeType { get; init; }

    /// <summary>Resource URI (type "resource").</summary>
    public string? Uri { get; init; }
}
