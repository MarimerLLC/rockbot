namespace RockBot.Tools.Mcp;

/// <summary>
/// Metadata for a single MCP tool within an <see cref="McpToolsAvailable"/> message.
/// </summary>
public sealed record McpToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// JSON Schema describing the tool's parameters, or null if none.
    /// </summary>
    public string? ParametersSchema { get; init; }
}
