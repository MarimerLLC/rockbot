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
    /// Default timeout in milliseconds for MCP server calls.
    /// </summary>
    public int DefaultTimeoutMs { get; set; } = 30_000;
}
