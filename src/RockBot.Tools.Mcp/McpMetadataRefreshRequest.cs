namespace RockBot.Tools.Mcp;

/// <summary>
/// Published by an agent to request the MCP Bridge to re-discover tools.
/// </summary>
public sealed record McpMetadataRefreshRequest
{
    /// <summary>
    /// Specific MCP server to refresh, or null to refresh all servers.
    /// </summary>
    public string? ServerName { get; init; }
}
