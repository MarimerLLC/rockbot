namespace RockBot.Tools.Mcp;

/// <summary>
/// Configuration for MCP tool integration.
/// </summary>
public sealed class McpOptions
{
    /// <summary>
    /// MCP server configurations to connect to.
    /// </summary>
    public List<McpServerConfig> Servers { get; } = [];
}
