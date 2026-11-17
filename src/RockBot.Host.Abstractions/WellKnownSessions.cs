namespace RockBot.Host;

/// <summary>
/// Well-known session identifiers shared across host components.
/// </summary>
public static class WellKnownSessions
{
    /// <summary>
    /// The primary Blazor UI user session. Used by idle-detection and
    /// availability checks to determine whether the user is actively engaged.
    /// </summary>
    public const string Primary = "blazor-session";

    /// <summary>
    /// Session carrying unsolicited output from system-owned scheduled work — system scheduled
    /// tasks and the memory audit's attention alerts. Frontends categorize this separately from
    /// user-requested scheduled tasks so housekeeping does not read as a reply.
    /// </summary>
    public const string ScheduledSystem = "scheduled-system";

    /// <summary>Session carrying the output of the user's own scheduled tasks.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Session carrying the idle-flushed batch of inbound A2A notifications.</summary>
    public const string A2AInbound = "a2a-inbound";

    /// <summary>
    /// Sessions whose replies go to every connected client rather than one conversation. Their
    /// replies are stored under these ids (#809) so a reloaded thread's history can include them;
    /// no agent loop runs in them.
    /// </summary>
    public static IReadOnlyList<string> Broadcast { get; } = [Scheduled, ScheduledSystem, A2AInbound];

    /// <summary>
    /// Whether <paramref name="sessionId"/> is a broadcast session: one of <see cref="Broadcast"/>, or
    /// an <c>a2a-inbound/…</c> session, which the user proxies also send to every client.
    /// </summary>
    public static bool IsBroadcast(string? sessionId) =>
        sessionId is Scheduled or ScheduledSystem or A2AInbound
        || sessionId?.StartsWith(A2AInbound + "/", StringComparison.Ordinal) == true;

    /// <summary>
    /// Prefix of the session a scheduled task's agent loop runs in (<c>patrol/{task name}</c>).
    /// </summary>
    public const string ScheduledTaskPrefix = "patrol/";

    /// <summary>
    /// The scheduled task <paramref name="sessionId"/> belongs to (<c>patrol/{task}</c>, in either
    /// session-id form), or null when it is not a scheduled task's session.
    /// </summary>
    public static string? ScheduledTaskName(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;
        var raw = UserProxy.SessionIdForms.Raw(sessionId);
        return raw.Length > ScheduledTaskPrefix.Length && raw.StartsWith(ScheduledTaskPrefix, StringComparison.Ordinal)
            ? raw[ScheduledTaskPrefix.Length..]
            : null;
    }
}
