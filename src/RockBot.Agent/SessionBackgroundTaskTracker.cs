using System.Collections.Concurrent;

namespace RockBot.Agent;

/// <summary>
/// Tracks one in-flight background tool loop per conversation session.
/// When a new user message arrives, the previous loop's CancellationToken is
/// cancelled so stale tool calls (e.g. an email send from a prior topic) cannot
/// execute after the user has already moved on to a different subject.
/// </summary>
internal sealed class SessionBackgroundTaskTracker : IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();

    /// <summary>
    /// Cancels any in-flight background loop for <paramref name="sessionId"/> and returns
    /// a new <see cref="CancellationToken"/> that is:
    /// <list type="bullet">
    ///   <item>linked to <paramref name="hostCt"/> so it cancels on host shutdown, and</item>
    ///   <item>cancelled the next time <see cref="BeginSession"/> is called for the same session.</item>
    /// </list>
    /// </summary>
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
