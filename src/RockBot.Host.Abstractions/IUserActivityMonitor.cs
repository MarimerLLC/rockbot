namespace RockBot.Host;

/// <summary>
/// Tracks when the most recent user message was received.
/// Used by low-priority background services (dreaming, session evaluation)
/// to back off while the user is actively waiting for a response.
/// </summary>
public interface IUserActivityMonitor
{
    /// <summary>
    /// Records that a user message has just arrived. Call this at the start
    /// of every <c>UserMessage</c> handler invocation.
    /// </summary>
    void RecordActivity();

    /// <summary>
    /// Returns true when a user message was received within <paramref name="idleThreshold"/>
    /// of the current time — i.e. the user is likely still waiting for a response.
    /// Background services should delay their work while this returns true.
    /// </summary>
    bool IsUserActive(TimeSpan idleThreshold);
}
