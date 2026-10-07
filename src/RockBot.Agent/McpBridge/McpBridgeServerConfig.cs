using RockBot.Agent.McpBridge.ArgGuards;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.McpBridge;

/// <summary>
/// Configuration for a single MCP server in the bridge's mcp.json.
/// </summary>
public sealed class McpBridgeServerConfig
{
    /// <summary>
    /// Stable id of this entry (<see cref="RockBot.Tools.Mcp.McpServerNames.NewId"/>). The bridge
    /// assigns one to any entry without it on load and persists it. It survives restarts and
    /// reconnects, but not a rename: consumers that must follow a server across renames store the
    /// id next to the name and re-resolve.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Who created this entry. <see cref="AgentOrigin"/> marks one the agent added with
    /// <c>mcp_register_server</c>; anything else, including no value, is the operator's (#603).
    /// Only the bridge sets it — the register request has no such field.
    /// </summary>
    public string? Origin { get; set; }

    /// <summary>The <see cref="Origin"/> of an entry created by <c>mcp_register_server</c>.</summary>
    public const string AgentOrigin = "agent";

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
    /// Optional timeout in milliseconds for tool calls to this server.
    /// When omitted, the bridge's DefaultTimeoutMs applies.
    /// </summary>
    public int? ToolTimeoutMs { get; set; }

    /// <summary>
    /// HTTP headers to include on every request to this server.
    /// Values may use <c>${ENV_VAR_NAME}</c> syntax for environment variable substitution.
    /// Example: <c>"X-Api-Key": "${MY_API_KEY}"</c>
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>
    /// Optional attachment-passthrough manifest. When set, the bridge transforms attachment
    /// arguments and responses for this server (see <see cref="AttachmentManifest"/>).
    /// Excluded from <see cref="CanonicalIdentity"/> because the manifest changes how the
    /// server is invoked, not which server is being talked to.
    /// </summary>
    public AttachmentManifest? Attachments { get; set; }

    /// <summary>
    /// Optional per-server argument guards applied by the bridge before forwarding a
    /// tool call (see <c>design/mcp-arg-guards.md</c>). Excluded from
    /// <see cref="CanonicalIdentity"/> for the same reason as <see cref="Attachments"/>:
    /// guards are policy about how the server is invoked, not which server it is.
    /// </summary>
    public List<McpArgGuardConfig> ArgGuards { get; set; } = [];

    /// <summary>
    /// Optional policy for <c>elicitation/create</c> — the question this server may ask the
    /// client mid-tool-call. When omitted, the bridge's <c>DefaultElicitation</c> applies.
    /// Excluded from <see cref="CanonicalIdentity"/> for the same reason as
    /// <see cref="ArgGuards"/>: it is policy about how the server is talked to, not which
    /// server it is.
    /// </summary>
    public McpElicitationConfig? Elicitation { get; set; }

    /// <summary>
    /// Optional bearer-token authentication. When set, the bridge resolves
    /// <see cref="McpServerAuthConfig.Profile"/> against the token provider
    /// registry and wires a <c>BearerInjectionHandler</c> into the HTTP client
    /// so every request carries a fresh access token.
    /// </summary>
    public McpServerAuthConfig? Auth { get; set; }

    /// <summary>
    /// True when the agent may unregister this entry: it created the entry with
    /// <c>mcp_register_server</c>, and the entry carries nothing that tool can't express (#603).
    /// </summary>
    /// <remarks>
    /// <c>mcp_register_server</c> and <c>mcp_unregister_server</c> are LLM-callable, and the
    /// bridge persists what they do to <c>mcp.json</c>. Every other entry is the operator's: one
    /// seeded from <c>McpBridge:DefaultServers</c>, written into <c>mcp.json</c> by hand, or
    /// created by the agent and since given policy by the operator — tool filters, headers, auth,
    /// guards, elicitation, attachments, a timeout or a transport mode. The model must not be
    /// able to shed any of that by unregistering and registering again, so it can't touch such an
    /// entry at all. The operator changes those entries through configuration.
    /// </remarks>
    public bool IsAgentOwned() =>
        string.Equals(Origin, AgentOrigin, StringComparison.OrdinalIgnoreCase) && !HasOperatorPolicy();

    /// <summary>True when this entry sets anything <c>mcp_register_server</c> can't.</summary>
    public bool HasOperatorPolicy() =>
        AllowedTools is { Count: > 0 }
        || DeniedTools is { Count: > 0 }
        || Headers is { Count: > 0 }
        || Auth is not null
        || ArgGuards is { Count: > 0 }
        || Elicitation is not null
        || Attachments is not null
        || ToolTimeoutMs is not null
        || !string.Equals(TransportMode?.Trim() ?? "auto", "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this config uses HTTP-based transport (SSE or streamable HTTP).
    /// </summary>
    public bool IsSse => Type?.ToLowerInvariant() is "sse" or "http" or "streamable-http";

    /// <summary>
    /// Computes a stable identity string for this server configuration that excludes the
    /// server's dictionary name. Two entries with the same canonical identity point at the
    /// same underlying server with the same credentials and options, and should be treated
    /// as duplicates even if registered under different names.
    /// </summary>
    public string CanonicalIdentity()
    {
        var type = Type?.Trim().ToLowerInvariant() ?? string.Empty;
        var url = NormalizeUrl(Url);
        var transportMode = TransportMode?.Trim().ToLowerInvariant() ?? "auto";
        var command = Command?.Trim() ?? string.Empty;
        var args = string.Join("", Args);
        var env = string.Join("", Env
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        var headers = string.Join("", Headers
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key.ToLowerInvariant()}={kvp.Value}"));
        var allowedTools = string.Join("", AllowedTools.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        var deniedTools = string.Join("", DeniedTools.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        // Include the auth profile so an authenticated entry is never deduped
        // against an unauthenticated one at the same URL.
        var authProfile = Auth?.Profile?.Trim().ToLowerInvariant() ?? string.Empty;
        return string.Join("", type, url, transportMode, command, args, env, headers, allowedTools, deniedTools, authProfile);
    }

    /// <summary>
    /// Normalizes a URL for duplicate detection: lowercases the scheme and authority,
    /// preserves path case, and strips a trailing slash. Returns empty string for null/blank.
    /// </summary>
    internal static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var authority = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
            var pathAndQuery = uri.PathAndQuery;
            return (authority + pathAndQuery).TrimEnd('/');
        }
        return trimmed.TrimEnd('/').ToLowerInvariant();
    }
}
