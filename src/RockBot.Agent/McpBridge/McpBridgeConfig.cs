namespace RockBot.Agent.McpBridge;

/// <summary>
/// Root configuration model for mcp.json.
/// </summary>
public sealed class McpBridgeConfig
{
    /// <summary>
    /// Map of server name to server configuration.
    /// </summary>
    public Dictionary<string, McpBridgeServerConfig> McpServers { get; set; } = [];
}
