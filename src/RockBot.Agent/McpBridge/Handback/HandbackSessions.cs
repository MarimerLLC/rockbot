namespace RockBot.Agent.McpBridge.Handback;

/// <summary>
/// The session ids a handed-back call carries. A tool call's <c>SessionId</c> is the run's
/// working-memory namespace (<c>session/{id}</c> for a user conversation); conversation memory
/// and replies to the user use the bare id.
/// </summary>
internal static class HandbackSessions
{
    private const string SessionPrefix = "session/";

    /// <summary>
    /// True for a user conversation, the only kind of session a restart notice is delivered to.
    /// Subagent sessions (<c>session/subagent-…</c>), patrols and wisps are not.
    /// </summary>
    public static bool IsUserSession(string? sessionId) =>
        sessionId is not null
        && sessionId.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
        && !sessionId.StartsWith(SessionPrefix + "subagent-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The bare id conversation memory and user replies are keyed by.</summary>
    public static string ConversationId(string sessionId) =>
        sessionId.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
            ? sessionId[SessionPrefix.Length..]
            : sessionId;

    /// <summary>Full working-memory key of a restart's interruption notice for <paramref name="entry"/>.</summary>
    public static string InterruptedKey(PendingQuestionEntry entry) =>
        $"{entry.SessionId}/{HandbackText.InterruptedKeyPrefix}{entry.QuestionId}";
}
