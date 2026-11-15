namespace RockBot.UserProxy.ChatState;

/// <summary>
/// The categorization rules a reply's source and session settle, shared by live replies
/// (<c>BlazorUserFrontend</c>) and turns reloaded from history (<see cref="ChatReducer"/>), so the
/// same message lands in the same category either way (#809).
/// </summary>
public static class ReplyCategories
{
    // The agent's broadcast sessions (RockBot.Host.WellKnownSessions, which this library doesn't reference).
    public const string ScheduledSession = "scheduled";
    public const string ScheduledSystemSession = "scheduled-system";
    public const string A2AInboundSession = "a2a-inbound";

    /// <summary>Whether <paramref name="sessionId"/> is the inbound A2A session or one of its <c>a2a-inbound/…</c> sessions.</summary>
    public static bool IsA2AInbound(string? sessionId) =>
        sessionId == A2AInboundSession
        || sessionId?.StartsWith(A2AInboundSession + "/", StringComparison.Ordinal) == true;

    /// <summary>
    /// The category <paramref name="source"/> and <paramref name="sessionId"/> give a reply, or null
    /// when they don't settle it (an older agent that sends no source, outside the scheduled
    /// sessions): the caller then decides by agent name.
    /// </summary>
    public static MessageCategory? FromSource(ReplySource? source, string? sessionId, bool isFinal)
    {
        switch (source?.Kind)
        {
            case ReplySourceKind.Subagent or ReplySourceKind.Worker:
                return MessageCategory.SubagentActivity;
            case ReplySourceKind.A2A:
                // Inbound notifications are A2A activity. The A2A result handlers, though, post the
                // agent's own answer in the user's session: that is the agent's reply, and their
                // progress is told apart (the agent's own vs a remote agent's) by name.
                if (IsA2AInbound(sessionId))
                    return MessageCategory.A2AActivity;
                if (isFinal)
                    return MessageCategory.PrimaryFinal;
                break;
            case ReplySourceKind.Scheduled:
                // Background system jobs and the user's own scheduled tasks share the kind; the
                // session tells them apart.
                return sessionId == ScheduledSystemSession
                    ? MessageCategory.ScheduledSystem
                    : MessageCategory.ScheduledUser;
            case ReplySourceKind.Primary or ReplySourceKind.Bridge:
                return isFinal ? MessageCategory.PrimaryFinal : MessageCategory.PrimaryProgress;
        }

        return sessionId switch
        {
            ScheduledSystemSession => MessageCategory.ScheduledSystem,
            ScheduledSession => MessageCategory.ScheduledUser,
            _ => null,
        };
    }
}
