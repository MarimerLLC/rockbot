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
/// Bridge response carrying all tool and prompt definitions for the requested server,
/// plus the server's self-reported identity from the MCP <c>initialize</c> handshake.
/// </summary>
public sealed record McpGetServiceDetailsResponse
{
    public required string ServerName { get; init; }

    /// <summary>Server's self-reported implementation name (from <c>initialize.result.serverInfo.name</c>).</summary>
    public string? ImplementationName { get; init; }

    /// <summary>Server's self-reported display title.</summary>
    public string? Title { get; init; }

    /// <summary>Server's self-reported version string.</summary>
    public string? Version { get; init; }

    /// <summary>Server's self-reported implementation description.</summary>
    public string? Description { get; init; }

    /// <summary>Free-text usage instructions supplied by the server during initialize.</summary>
    public string? Instructions { get; init; }

    public List<McpToolDefinition> Tools { get; init; } = [];
    public List<McpPromptDefinition> Prompts { get; init; } = [];
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

// ── GetPrompt ────────────────────────────────────────────────────────────────

/// <summary>
/// Requests a filled-in prompt template from an MCP server.
/// Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpGetPromptRequest
{
    public required string ServerName { get; init; }
    public required string PromptName { get; init; }
    public Dictionary<string, string> Arguments { get; init; } = [];
}

/// <summary>Bridge response carrying the filled-in prompt messages.</summary>
public sealed record McpGetPromptResponse
{
    public required string ServerName { get; init; }
    public required string PromptName { get; init; }
    public string? Description { get; init; }
    public List<McpPromptMessage> Messages { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>A single message from a filled-in MCP prompt template.</summary>
public sealed record McpPromptMessage
{
    public required string Role { get; init; }       // "user" or "assistant"
    public required string Content { get; init; }    // text content (most common case)
    public string ContentType { get; init; } = "text";
}
