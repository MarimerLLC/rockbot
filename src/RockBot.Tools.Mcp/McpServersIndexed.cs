namespace RockBot.Tools.Mcp;

/// <summary>
/// Published by the MCP Bridge when its server inventory changes.
/// Agents receive concise server summaries and register a fixed set of management
/// tools — they drill into individual tool schemas via <c>mcp_get_service_details</c>
/// on demand rather than receiving every schema at once.
/// </summary>
public sealed record McpServersIndexed
{
    /// <summary>
    /// Servers that are currently available (added or updated).
    /// </summary>
    public required List<McpServerSummary> Servers { get; init; }

    /// <summary>
    /// Names of servers that have been removed since the last index update.
    /// </summary>
    public List<string> RemovedServers { get; init; } = [];
}
