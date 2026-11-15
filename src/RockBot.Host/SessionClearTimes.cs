namespace RockBot.Host;

/// <summary>
/// When each session's context was last cleared. A thread's history merges in the broadcast
/// sessions' stored replies (#809); those are shared by every thread, so a clear can't delete
/// them. Instead a cleared thread's history leaves out the ones from before its clear.
/// <para>
/// In memory, like <see cref="SessionOriginStore"/>: after a restart a cleared thread gets the
/// broadcast replies from before its clear back, bounded by the broadcast sessions' turn window.
/// </para>
/// </summary>
public sealed class SessionClearTimes
{
    private readonly Dictionary<string, DateTimeOffset> _byId = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Records that <paramref name="sessionId"/> was cleared at <paramref name="at"/>.</summary>
    public void Set(string sessionId, DateTimeOffset at)
    {
        lock (_lock)
            _byId[sessionId] = at;
    }

    /// <summary>When <paramref name="sessionId"/> was last cleared, or null.</summary>
    public DateTimeOffset? Get(string sessionId)
    {
        lock (_lock)
            return _byId.TryGetValue(sessionId, out var at) ? at : null;
    }
}
