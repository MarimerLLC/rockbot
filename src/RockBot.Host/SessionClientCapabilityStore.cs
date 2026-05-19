using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// In-memory cache of the currently-active rendering capabilities for each user
/// session. Written on every inbound <see cref="UserMessage"/> arrival (last
/// writer wins, so a user that switches clients mid-conversation flips the
/// cached capability on their next turn). Read by entry points that produce
/// user-facing replies but don't have the originating <see cref="UserMessage"/>
/// in scope — A2A handlers, subagent runner. Cleared by
/// <see cref="ClearContextHandler"/> when the user resets the session.
/// <para>
/// This is intentionally separate from <see cref="ISessionTracker"/> /
/// <see cref="SessionBackgroundTaskTracker"/>: that tracks per-loop cancellation
/// generations and clears entries on <c>EndSession</c>; capability metadata must
/// persist across loops (A2A callbacks may fire long after the user's loop
/// completes). Singleton; lifetime spans the agent process.
/// </para>
/// </summary>
public sealed class SessionClientCapabilityStore
{
    private readonly Dictionary<string, ClientCapabilities> _byId
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>
    /// Set or replace the cached capability for <paramref name="sessionId"/>.
    /// Passing <see cref="ClientCapabilities.None"/> removes the entry —
    /// preserves the invariant that a missing entry and a <c>None</c>-only
    /// entry behave identically.
    /// </summary>
    public void Set(string sessionId, ClientCapabilities caps)
    {
        lock (_lock)
        {
            if (caps == ClientCapabilities.None)
                _byId.Remove(sessionId);
            else
                _byId[sessionId] = caps;
        }
    }

    /// <summary>
    /// Returns the cached capability, or <see cref="ClientCapabilities.None"/>
    /// if the session has not advertised one.
    /// </summary>
    public ClientCapabilities Get(string sessionId)
    {
        lock (_lock)
            return _byId.GetValueOrDefault(sessionId);
    }

    /// <summary>
    /// Remove any cached entry for <paramref name="sessionId"/>. Called from
    /// <c>ClearContextHandler</c> when the user resets the conversation.
    /// </summary>
    public void Clear(string sessionId)
    {
        lock (_lock)
            _byId.Remove(sessionId);
    }
}
