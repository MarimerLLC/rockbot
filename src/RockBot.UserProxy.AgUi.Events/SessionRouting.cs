namespace RockBot.UserProxy.AgUi;

/// <summary>
/// Which AG-UI thread (RockBot session) a piece of broadcast traffic belongs to. Every user proxy
/// hears every broadcast, so a frontend that shows one session has to pick out its own.
/// </summary>
public static class SessionRouting
{
    /// <summary>The prefix the agent's <c>SessionIds.Raw</c> removes.</summary>
    public const string NamespacedSessionPrefix = "session/";

    /// <summary>The session a user's scheduled tasks reply in (<c>ScheduledTaskHandler</c>).</summary>
    public const string ScheduledSession = "scheduled";

    /// <summary>
    /// The session system-owned scheduled work replies in. The same value as
    /// <c>RockBot.Host.WellKnownSessions.ScheduledSystem</c>, which this library doesn't reference.
    /// </summary>
    public const string ScheduledSystemSession = "scheduled-system";

    /// <summary>
    /// The session inbound A2A notifications are delivered in (<c>InboundNotificationService</c>).
    /// An inbound A2A task's own session is <c>a2a-inbound/&lt;context id&gt;</c>.
    /// </summary>
    public const string A2AInboundSession = "a2a-inbound";

    /// <summary>
    /// True when a reply with <paramref name="sessionId"/> belongs to <paramref name="threadId"/>.
    /// <see cref="AgentReply.SessionId"/> is the raw id, except that a subagent's or worker's tool
    /// progress keeps the agent's working-memory form, <c>session/&lt;raw id&gt;</c>. Exactly those
    /// two forms match, and no more than that one prefix is stripped, so a different session
    /// never does.
    /// </summary>
    public static bool IsSessionsReply(string threadId, string? sessionId) =>
        string.Equals(sessionId, threadId, StringComparison.Ordinal)
        || (sessionId is not null
            && sessionId.Length == NamespacedSessionPrefix.Length + threadId.Length
            && sessionId.StartsWith(NamespacedSessionPrefix, StringComparison.Ordinal)
            && sessionId.EndsWith(threadId, StringComparison.Ordinal));

    /// <summary>
    /// True for the sessions that belong to no one conversation, whose output every client shows
    /// (as the Blazor UI does): scheduled tasks, system scheduled work and inbound A2A.
    /// </summary>
    public static bool IsBroadcastSession(string? sessionId) =>
        sessionId is ScheduledSession or ScheduledSystemSession or A2AInboundSession
        || sessionId?.StartsWith(A2AInboundSession + "/", StringComparison.Ordinal) == true;
}
