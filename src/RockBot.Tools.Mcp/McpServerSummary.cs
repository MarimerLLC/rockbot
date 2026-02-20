namespace RockBot.Tools.Mcp;

/// <summary>
/// A brief summary of a single MCP server's capabilities.
/// Sent in <see cref="McpServersIndexed"/> so agents get a high-level index
/// without receiving every tool schema up front.
/// </summary>
public sealed record McpServerSummary
{
    public required string ServerName { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>
    /// LLM-generated (or fallback) description of what this server provides.
    /// </summary>
    public string? Summary { get; init; }

    public int ToolCount { get; init; }
    public List<string> ToolNames { get; init; } = [];
}
