namespace RockBot.Tools.Mcp;

/// <summary>
/// Published by the MCP Bridge when its tool inventory changes.
/// The agent host uses this to update its local <see cref="IToolRegistry"/>.
/// </summary>
public sealed record McpToolsAvailable
{
    /// <summary>
    /// Name of the MCP server that provides these tools.
    /// </summary>
    public required string ServerName { get; init; }

    /// <summary>
    /// Tools currently available from this server.
    /// </summary>
    public required List<McpToolDefinition> Tools { get; init; }

    /// <summary>
    /// Tool names that were previously available but have been removed.
    /// </summary>
    public required List<string> RemovedTools { get; init; }
}
