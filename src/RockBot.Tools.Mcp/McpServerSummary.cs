namespace RockBot.Tools.Mcp;

/// <summary>
/// A brief summary of a single MCP server's capabilities.
/// Sent in <see cref="McpServersIndexed"/> so agents get a high-level index
/// without receiving every tool schema up front.
/// </summary>
public sealed record McpServerSummary
{
    public required string ServerName { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>
    /// LLM-generated (or fallback) description of what this server provides.
    /// </summary>
    public string? Summary { get; init; }

    public int ToolCount { get; init; }
    public List<string> ToolNames { get; init; } = [];
    public int PromptCount { get; init; }
    public List<string> PromptNames { get; init; } = [];

    /// <summary>
    /// Stable id of the server entry. Unlike <see cref="ServerName"/> it never changes for the
    /// life of the entry; a rename is an unregister plus a register and gets a new id.
    /// </summary>
    public string? ServerId { get; init; }

    /// <summary>
    /// <see cref="McpSurfaceFingerprint.Server"/> of the tools and prompts this summary describes,
    /// or null when the surface couldn't be read in full (freshness unknown).
    /// </summary>
    public string? Fingerprint { get; init; }

    /// <summary>Per-tool <see cref="McpSurfaceFingerprint.Tool"/>, keyed by tool name.</summary>
    public Dictionary<string, string> ToolFingerprints { get; init; } = [];

    /// <summary>Server-reported <c>serverInfo.version</c>, or null when the server didn't report one.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// Short hash of the server entry's canonical identity (transport, URL or command, auth
    /// profile, tool filters). Two names with the same hash point at the same server, which is
    /// how a skill written for a since-renamed server is recognised. Only the hash travels; the
    /// identity itself can contain secrets.
    /// </summary>
    public string? IdentityHash { get; init; }
}
