namespace RockBot.UserProxy.ChatState;

/// <summary>How much of the agent's working detail a front end draws in the transcript (#828).</summary>
public enum ChatDetailLevel
{
    /// <summary>
    /// What is meant for the person: their messages, RockBot's final replies, results of their
    /// own scheduled tasks, errors, saved references and MCP questions. The default.
    /// </summary>
    Simple,

    /// <summary>
    /// Everything: also subagent, worker, inbound A2A and system background-task bubbles, activity
    /// logs with their tool calls, and interim lines.
    /// </summary>
    Detailed,
}

/// <summary>
/// Which parts of a <see cref="ChatViewState"/> a front end draws at a <see cref="ChatDetailLevel"/>.
/// The level filters rendering only: the state holds everything either way, and
/// <see cref="ActivityDigest"/> shows the detail Simple leaves out.
/// </summary>
public static class ChatVisibility
{
    /// <summary>
    /// How long a subagent's result can go without a reply from RockBot before Simple shows it
    /// anyway. RockBot normally writes its own reply from the result; this covers the case where
    /// that reply never comes, so the person isn't left with nothing.
    /// </summary>
    public static readonly TimeSpan UnansweredResultGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The transcript as drawn at <paramref name="level"/>, in order. Detailed draws every message.
    /// </summary>
    /// <param name="utcNow">The time now, for <see cref="UnansweredResultGrace"/>.</param>
    public static IReadOnlyList<ChatMessage> VisibleMessages(ChatViewState state, ChatDetailLevel level, DateTime utcNow)
    {
        if (level == ChatDetailLevel.Detailed)
            return state.Messages;

        var messages = state.Messages;
        var visible = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (!IsAgentDetail(messages[i]) || IsUnansweredSubagentResult(messages, i, utcNow))
                visible.Add(messages[i]);
        }
        return visible;
    }

    /// <summary>
    /// Whether a message is the agent's working detail, which only Detailed draws: an activity
    /// log, an interim line, or a subagent / worker, inbound A2A or system background-task message.
    /// </summary>
    public static bool IsAgentDetail(ChatMessage message) =>
        message.IsActivityLog
        || message.IsInterim
        || message.Category is MessageCategory.PrimaryProgress
            or MessageCategory.SubagentActivity
            or MessageCategory.A2AActivity
            or MessageCategory.ScheduledSystem;

    /// <summary>
    /// Whether the message at <paramref name="index"/> is a subagent's or worker's result that
    /// RockBot never replied to: no reply from RockBot follows it, and either the person has
    /// sent another message since or <see cref="UnansweredResultGrace"/> has passed. Simple shows
    /// such a result rather than leave the person with nothing.
    /// </summary>
    public static bool IsUnansweredSubagentResult(IReadOnlyList<ChatMessage> messages, int index, DateTime utcNow)
    {
        var result = messages[index];
        if (result.Category != MessageCategory.SubagentActivity
            || result.IsActivityLog || result.IsInterim || result.IsFromUser)
            return false;

        for (var i = index + 1; i < messages.Count; i++)
        {
            var next = messages[i];
            if (IsPrimaryReply(next))
                return false;
            if (next.Category == MessageCategory.UserInput)
                return true;
        }

        return utcNow - result.Timestamp >= UnansweredResultGrace;
    }

    /// <summary>
    /// The task lists ("plans") drawn at <paramref name="level"/>: the primary turn's first, then
    /// subagents' by when they last changed. Simple draws only the primary turn's.
    /// </summary>
    public static IReadOnlyList<TaskListView> VisibleTaskLists(ChatViewState state, ChatDetailLevel level) =>
        state.TaskLists.Values
            .Where(l => level == ChatDetailLevel.Detailed || l.Key == ReplySourceKind.Primary)
            .OrderBy(l => l.Key == ReplySourceKind.Primary ? 0 : 1)
            .ThenBy(l => l.UpdatedAt)
            .ToList();

    /// <summary>One of RockBot's own final replies.</summary>
    internal static bool IsPrimaryReply(ChatMessage message) =>
        message.Category == MessageCategory.PrimaryFinal
        && !message.IsFromUser && !message.IsInterim && !message.IsActivityLog && !message.IsError;
}
