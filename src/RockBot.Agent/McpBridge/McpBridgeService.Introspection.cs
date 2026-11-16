using System.Text.RegularExpressions;

namespace RockBot.Agent.McpBridge;

/// <summary>One configured MCP server's state, for the info views (#834). Carries no credential.</summary>
internal sealed record McpServerStatus(
    string Name,
    string? DisplayName,
    string Transport,
    string? Host,
    string State,
    bool UsesOAuth,
    int ToolCount,
    int PromptCount,
    int ResourceCount,
    string? Version,
    string? LastError,
    DateTimeOffset? LastErrorAt);

public sealed partial class McpBridgeService
{
    /// <summary>The states <see cref="GetServerStatuses"/> reports.</summary>
    internal static class ServerStates
    {
        public const string Connected = "connected";
        public const string SignInRequired = "sign-in required";
        public const string SignInPending = "sign-in pending";
        public const string Failed = "failed";
        public const string NotConnected = "not connected";
    }

    /// <summary>
    /// Every configured server and its state, from the bridge's own registry: no network call and
    /// no disk read. Endpoints are a host only, and nothing from the config's headers, environment
    /// or auth is included.
    /// </summary>
    internal IReadOnlyList<McpServerStatus> GetServerStatuses()
    {
        var statuses = new List<McpServerStatus>();
        foreach (var name in _connections.ConfiguredNames.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!_connections.TryGetConfig(name, out var config))
                continue;

            _connections.TryGet(name, out var connected);
            var error = _lastConnectErrors.TryGetValue(name, out var e) ? e : ((string, DateTimeOffset)?)null;

            var state = _signInsByServer.ContainsKey(name) ? ServerStates.SignInPending
                : IsSignInRequired(name) ? ServerStates.SignInRequired
                : connected is not null ? ServerStates.Connected
                : error is not null ? ServerStates.Failed
                : ServerStates.NotConnected;

            statuses.Add(new McpServerStatus(
                name,
                connected?.Summary.DisplayName is { Length: > 0 } display && display != name ? display : null,
                !string.IsNullOrWhiteSpace(config.Url) ? "http" : !string.IsNullOrWhiteSpace(config.Command) ? "stdio" : config.Type ?? "unknown",
                HostOf(config.Url),
                state,
                config.Auth?.OAuth is not null,
                connected?.Tools.Count ?? 0,
                connected?.Prompts.Count ?? 0,
                connected?.DownstreamResources.Count ?? 0,
                connected?.Metadata.Version,
                // A connected server's earlier failure is history, not its state.
                connected is null ? error?.Item1 : null,
                connected is null ? error?.Item2 : null));
        }
        return statuses;
    }

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}")
            : null;

    /// <summary>
    /// A connect failure in a form safe to show: the innermost message, with any URL's query
    /// string and user info removed (a token can ride there), on one line, at most 300 characters.
    /// </summary>
    internal static string DescribeConnectError(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null)
            inner = inner.InnerException;

        var text = UrlSecrets().Replace(inner.Message, m => m.Groups["base"].Value + m.Groups["host"].Value);
        text = Whitespace().Replace(text, " ").Trim();
        if (text.Length == 0)
            text = inner.GetType().Name;
        return text.Length > 300 ? text[..297] + "..." : text;
    }

    // scheme://[userinfo@]host/path[?query][#fragment] → scheme://host/path
    [GeneratedRegex(@"(?<base>[a-z][a-z0-9+.-]*://)(?:[^\s/@]+@)?(?<host>[^\s?#""'<>]*)(?:[?#][^\s""'<>]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlSecrets();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
