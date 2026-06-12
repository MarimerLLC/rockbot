using RockBot.Agent.McpBridge.ArgGuards;
using RockBot.Agent.McpBridge.Attachments;

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
    /// Optional bearer-token authentication. When set, the bridge resolves
    /// <see cref="McpServerAuthConfig.Profile"/> against the token provider
    /// registry and wires a <c>BearerInjectionHandler</c> into the HTTP client
    /// so every request carries a fresh access token.
    /// </summary>
    public McpServerAuthConfig? Auth { get; set; }

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
