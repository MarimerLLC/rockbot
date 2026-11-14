using Microsoft.AspNetCore.WebUtilities;

namespace RockBot.UserProxy.AgUi;

/// <summary>
/// The MCP sign-ins this proxy started and whose browser redirect it still expects (#794, phase 4),
/// by the OAuth <c>state</c> in each one's authorization URL.
/// </summary>
/// <remarks>
/// <para>
/// The redirect URI (<c>/mcp-oauth/callback</c>) is the one endpoint besides <c>/healthz</c> that
/// stays anonymous once the proxy has app auth: an authorization server sends a browser there, and
/// a browser navigation carries no bearer token. So what arrives is checked against what this
/// proxy asked for: a redirect whose state it doesn't know is refused and never reaches the bus.
/// The agent's bridge makes the same check and is the one that matters for the sign-in itself (the
/// code is useless without the PKCE verifier, which never leaves the bridge); this one keeps an
/// open endpoint from being a way to publish to the bus at will.
/// </para>
/// <para>
/// In memory, like the rest of the proxy's state. After a restart, or on another replica, a
/// redirect for a sign-in started before is refused and the user starts it again.
/// </para>
/// </remarks>
internal sealed class McpSignInTracker(TimeProvider? clock = null)
{
    /// <summary>How many sign-ins may wait at once. The oldest is forgotten to make room.</summary>
    public const int MaxPending = 64;

    /// <summary>How long a redirect is expected when the agent didn't say when the sign-in expires.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Allowed past the agent's expiry, for a redirect already on its way.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    public sealed record PendingSignIn(string ServerName, string? SignInId, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, PendingSignIn> _byState = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Remembers a sign-in the agent agreed to start. False when its authorization URL carries no
    /// <c>state</c>, in which case its redirect could never be matched.
    /// </summary>
    public bool Track(McpSignInStartResponse response)
    {
        if (StateOf(response.AuthorizationUri) is not { } state)
            return false;

        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            Prune(now);
            if (_byState.Count >= MaxPending && !_byState.ContainsKey(state))
                _byState.Remove(_byState.MinBy(pair => pair.Value.StartedAt).Key);

            _byState[state] = new PendingSignIn(
                response.ServerName, response.SignInId, now, (response.ExpiresAt ?? now + DefaultLifetime) + Grace);
        }
        return true;
    }

    /// <summary>
    /// Takes the sign-in a redirect's <c>state</c> belongs to. Null when this proxy started no
    /// such sign-in, it has expired, or its redirect was already relayed: a state is good once.
    /// </summary>
    public PendingSignIn? Take(string? state)
    {
        if (string.IsNullOrEmpty(state))
            return null;

        lock (_lock)
        {
            Prune(_clock.GetUtcNow());
            return _byState.Remove(state, out var pending) ? pending : null;
        }
    }

    /// <summary>The <c>state</c> query parameter of an authorization URL, or null.</summary>
    internal static string? StateOf(string? authorizationUri)
    {
        if (!Uri.TryCreate(authorizationUri, UriKind.Absolute, out var uri))
            return null;

        return QueryHelpers.ParseQuery(uri.Query).TryGetValue("state", out var values)
               && values.ToString() is { Length: > 0 } state
            ? state
            : null;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var expired in _byState.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList())
            _byState.Remove(expired);
    }
}
