namespace RockBot.Agent.McpBridge;

/// <summary>
/// Configuration for a single MCP server in the bridge's mcp.json.
/// </summary>
public sealed class McpBridgeServerConfig
{
    /// <summary>
    /// Transport type: "sse" (only SSE is supported in this embedded mode).
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Command to launch (stdio transport — not supported in embedded mode).
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// Arguments for the command (stdio transport — not supported in embedded mode).
    /// </summary>
    public List<string> Args { get; set; } = [];

    /// <summary>
    /// Environment variables for the server process (stdio transport — not supported in embedded mode).
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
    /// HTTP transport mode: "auto" (default, negotiates with server), "sse" (legacy session-based
    /// SSE), or "streamable-http" (stateless per-request HTTP, preferred for modern servers).
    /// Only applies when <see cref="Type"/> is an HTTP-based transport.
    /// </summary>
    public string TransportMode { get; set; } = "auto";

    /// <summary>
    /// HTTP headers to include on every request to this server.
    /// Values may use <c>${ENV_VAR_NAME}</c> syntax for environment variable substitution.
    /// Example: <c>"X-RockBot-Token": "${Staging__Token}"</c>
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>
    /// Whether this config uses HTTP-based transport (SSE or streamable HTTP).
    /// </summary>
    public bool IsSse => Type?.ToLowerInvariant() is "sse" or "http" or "streamable-http";
}
