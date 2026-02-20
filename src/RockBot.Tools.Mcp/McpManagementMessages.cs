namespace RockBot.Tools.Mcp;

// ── GetServiceDetails ────────────────────────────────────────────────────────

/// <summary>
/// Requests the full tool schema list for one MCP server from the bridge.
/// Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpGetServiceDetailsRequest
{
    public required string ServerName { get; init; }
}

/// <summary>
/// Bridge response carrying all tool definitions for the requested server.
/// </summary>
public sealed record McpGetServiceDetailsResponse
{
    public required string ServerName { get; init; }
    public List<McpToolDefinition> Tools { get; init; } = [];
    public string? Error { get; init; }
}

// ── RegisterServer ───────────────────────────────────────────────────────────

/// <summary>
/// Requests the bridge to connect a new MCP server at runtime.
/// Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpRegisterServerRequest
{
    public required string ServerName { get; init; }
    public required string Type { get; init; }
    public string? Url { get; init; }
    public string? Command { get; init; }
    public List<string> Args { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = [];
}

/// <summary>Bridge response confirming or reporting failure for a server registration.</summary>
public sealed record McpRegisterServerResponse
{
    public required string ServerName { get; init; }
    public bool Success { get; init; }
    public string? Summary { get; init; }
    public string? Error { get; init; }
}

// ── UnregisterServer ─────────────────────────────────────────────────────────

/// <summary>
/// Requests the bridge to disconnect and remove an MCP server at runtime.
/// Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpUnregisterServerRequest
{
    public required string ServerName { get; init; }
}

/// <summary>Bridge response confirming or reporting failure for a server removal.</summary>
public sealed record McpUnregisterServerResponse
{
    public required string ServerName { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
