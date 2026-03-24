using System.Collections.Concurrent;

namespace RockBot.Host;

/// <summary>
/// Tracks one in-flight background tool loop per conversation session.
/// When a new user message arrives, the previous loop's CancellationToken is
/// cancelled so stale tool calls (e.g. an email send from a prior topic) cannot
/// execute after the user has already moved on to a different subject.
/// </summary>
internal sealed class SessionBackgroundTaskTracker : ISessionTracker, IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();

    /// <inheritdoc/>
    public CancellationToken BeginSession(string sessionId, CancellationToken hostCt)
    {
        // Cancel and discard the previous background loop for this session, if any.
        if (_sessions.TryRemove(sessionId, out var old))
        {
            old.Cancel();
            old.Dispose();
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        _sessions[sessionId] = cts;
        return cts.Token;
    }

    /// <inheritdoc/>
    public bool HasActiveUserLoop(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var cts) && !cts.IsCancellationRequested;

    public void Dispose()
    {
        foreach (var kvp in _sessions)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _sessions.Clear();
    }
}
