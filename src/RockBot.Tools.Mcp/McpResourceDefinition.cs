namespace RockBot.Tools.Mcp;

/// <summary>
/// One MCP resource or resource template a server lists (#617). For a template,
/// <see cref="Uri"/> is the RFC 6570 URI template, <c>{…}</c> expressions included, and
/// <see cref="Size"/> is always null.
/// </summary>
public sealed record McpResourceDefinition
{
    public required string Uri { get; init; }
    public required string Name { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? MimeType { get; init; }
    public long? Size { get; init; }
    public bool IsTemplate { get; init; }
}

/// <summary>
/// One item of a read resource's contents, as the model sees it: either <see cref="Text"/>
/// inline, or a file the bridge saved to the shared volume (<see cref="Path"/> and friends)
/// because the content was binary or too large to return inline.
/// </summary>
public sealed record McpResourceContentView
{
    public required string Uri { get; init; }
    public string? MimeType { get; init; }
    public string? Text { get; init; }
    public string? Path { get; init; }
    public string? Name { get; init; }
    public long? Size { get; init; }
    public string? Note { get; init; }
}
