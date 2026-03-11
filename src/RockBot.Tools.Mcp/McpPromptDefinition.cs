namespace RockBot.Tools.Mcp;

/// <summary>
/// Metadata for a single MCP prompt template within an <see cref="McpGetServiceDetailsResponse"/>.
/// </summary>
public sealed record McpPromptDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<McpPromptArgument> Arguments { get; init; } = [];
}

/// <summary>
/// Describes a single argument for an <see cref="McpPromptDefinition"/>.
/// </summary>
public sealed record McpPromptArgument
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
}
