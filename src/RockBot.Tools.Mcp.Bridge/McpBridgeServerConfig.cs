namespace RockBot.Tools.Mcp.Bridge;

/// <summary>
/// Configuration for a single MCP server in the bridge's mcp.json.
/// </summary>
public sealed class McpBridgeServerConfig
{
    /// <summary>
    /// Transport type: "stdio" (default) or "sse".
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Command to launch (stdio transport).
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// Arguments for the command (stdio transport).
    /// </summary>
    public List<string> Args { get; set; } = [];

    /// <summary>
    /// Environment variables for the server process (stdio transport).
    /// </summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>
    /// URL to connect to (SSE transport).
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// If specified, only these tools are allowed. Takes precedence over <see cref="DeniedTools"/>.
    /// </summary>
    public List<string> AllowedTools { get; set; } = [];

    /// <summary>
    /// Tools to exclude. Ignored if <see cref="AllowedTools"/> is non-empty.
    /// </summary>
    public List<string> DeniedTools { get; set; } = [];

    /// <summary>
    /// Whether this config uses SSE transport.
    /// </summary>
    public bool IsSse => string.Equals(Type, "sse", StringComparison.OrdinalIgnoreCase);
}
