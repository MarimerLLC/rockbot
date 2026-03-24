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
    private readonly ConcurrentDictionary<string, long> _activeGenerations = new();
    private long _nextGeneration;

    /// <inheritdoc/>
    public SessionHandle BeginSession(string sessionId, CancellationToken hostCt)
    {
        // Cancel and discard the previous background loop for this session, if any.
        if (_sessions.TryRemove(sessionId, out var old))
        {
            old.Cancel();
            old.Dispose();
        }

        var generation = Interlocked.Increment(ref _nextGeneration);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        _sessions[sessionId] = cts;
        _activeGenerations[sessionId] = generation;
        return new SessionHandle(cts.Token, generation);
    }

    /// <inheritdoc/>
    public void EndSession(string sessionId, long generation)
    {
        // Atomically remove only if the generation matches — a stale EndSession
        // from a superseded loop cannot deactivate a newer one.
        _activeGenerations.TryRemove(new KeyValuePair<string, long>(sessionId, generation));
    }

    /// <inheritdoc/>
    public bool HasActiveUserLoop(string sessionId) =>
        _activeGenerations.ContainsKey(sessionId);

    public void Dispose()
    {
        foreach (var kvp in _sessions)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _sessions.Clear();
        _activeGenerations.Clear();
    }
}
