namespace RockBot.Host;

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
    /// returns a new <see cref="CancellationToken"/> that is linked to
    /// <paramref name="hostCt"/> and will be cancelled the next time this method
    /// is called for the same session.
    /// </summary>
    CancellationToken BeginSession(string sessionId, CancellationToken hostCt);

    /// <summary>
    /// Returns <c>true</c> when a user-message processing loop is currently
    /// active for the given <paramref name="sessionId"/>.
    /// </summary>
    bool HasActiveUserLoop(string sessionId);
}
