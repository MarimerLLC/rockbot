namespace RockBot.Cli.McpBridge;

/// <summary>
/// Options for the MCP Bridge service.
/// </summary>
public sealed class McpBridgeOptions
{
    /// <summary>
    /// Path to the mcp.json configuration file.
    /// </summary>
    public string ConfigPath { get; set; } = "mcp.json";

    /// <summary>
    /// Agent name this bridge serves. Used for topic routing.
    /// </summary>
    public string AgentName { get; set; } = "default-agent";

    /// <summary>
    /// Default timeout in milliseconds for MCP server calls.
    /// </summary>
    public int DefaultTimeoutMs { get; set; } = 30_000;
}
