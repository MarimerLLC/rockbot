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
    /// Copies the operator-only policy that <c>register_mcp_server</c> cannot express from the
    /// config this one replaces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>register_mcp_server</c> is LLM-callable. Re-registering an existing name must not let
    /// the model shed policy the operator declared — dropping <see cref="Elicitation"/> would
    /// fall back to <c>DefaultElicitation</c>, turning an <c>off</c> server into an answering
    /// one and discarding its <c>deniedFields</c>.
    /// </para>
    /// <para>
    /// Restrictions and grants are carried differently. Restrictions (<see cref="ArgGuards"/>,
    /// the elicitation mode, <c>deniedFields</c>, <c>maxPerCall</c>) always carry over. Grants —
    /// an elicitation <c>responder</c> that may hand over conversation data, and <c>defaults</c>,
    /// which answer on the user's behalf and can pre-answer confirmations — belong to the server
    /// the operator configured, so they carry over only when the re-registration still points at
    /// that server (same <see cref="EndpointIdentity"/>). Otherwise the model could re-point a
    /// trusted name at a URL of its choosing and inherit what the operator granted.
    /// </para>
    /// </remarks>
    public void CarryOperatorPolicyFrom(McpBridgeServerConfig? existing)
    {
        if (existing is null) return;
        ArgGuards = existing.ArgGuards;

        var sameServer = string.Equals(EndpointIdentity(), existing.EndpointIdentity(), StringComparison.Ordinal);
        Elicitation = sameServer ? existing.Elicitation : existing.Elicitation?.WithoutGrants();
    }

    /// <summary>
    /// Which server this config talks to — transport type, URL, or command, arguments and
    /// environment — and nothing about how it is talked to.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CanonicalIdentity"/>, this leaves out tool filters, headers, auth profile
    /// and transport mode. <c>register_mcp_server</c> cannot express any of those, so including
    /// them would make every filtered or authenticated server look like a different server when
    /// the model re-registers it at the same address.
    /// </remarks>
    public string EndpointIdentity()
    {
        var type = Type?.Trim().ToLowerInvariant() ?? string.Empty;
        var url = NormalizeUrl(Url);
        var command = Command?.Trim() ?? string.Empty;
        var args = string.Join("\u001f", Args ?? []);
        var env = string.Join("\u001f", (Env ?? [])
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return string.Join("\u001e", type, url, command, args, env);
    }

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
