namespace RockBot.Host;

/// <summary>
/// A token returned by <see cref="ISessionTracker.BeginSession"/> that carries
/// both the cancellation token for the session loop and a generation identifier
/// used to safely call <see cref="ISessionTracker.EndSession"/>.
/// </summary>
public readonly record struct SessionHandle(CancellationToken Token, long Generation);

/// <summary>
/// Tracks active user-message processing loops per session.
/// <para>
/// Agent-side user message handlers call <see cref="BeginSession"/> when a new
/// message arrives; framework-level background handlers (subagent results, A2A
/// completions, etc.) call <see cref="HasActiveUserLoop"/> to detect whether the
/// user has moved on to a new message since background work was initiated.
/// </para>
/// </summary>
public interface ISessionTracker
{
    /// <summary>
    /// Cancels any in-flight background loop for <paramref name="sessionId"/> and
    /// returns a <see cref="SessionHandle"/> containing a new
    /// <see cref="CancellationToken"/> (linked to <paramref name="hostCt"/>) and
    /// a generation identifier. The token will be cancelled the next time this
    /// method is called for the same session.
    /// </summary>
    SessionHandle BeginSession(string sessionId, CancellationToken hostCt);

    /// <summary>
    /// Marks the processing loop for <paramref name="sessionId"/> as complete.
    /// Only takes effect when <paramref name="generation"/> matches the current
    /// generation — a stale call from an earlier loop that was superseded by a
    /// newer <see cref="BeginSession"/> is safely ignored.
    /// </summary>
    void EndSession(string sessionId, long generation);

    /// <summary>
    /// Returns <c>true</c> when a user-message processing loop is currently
    /// active for the given <paramref name="sessionId"/>.
    /// </summary>
    bool HasActiveUserLoop(string sessionId);
}
