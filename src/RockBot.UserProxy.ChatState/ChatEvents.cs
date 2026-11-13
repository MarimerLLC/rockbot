namespace RockBot.UserProxy.ChatState;

/// <summary>
/// Something that happened to a chat: agent output, a turn starting or ending, or a user action.
/// <see cref="ChatReducer"/> folds a sequence of these into a <see cref="ChatViewState"/>, and
/// <see cref="ChatStateService.Apply"/> is the one way a front end changes its chat state.
/// </summary>
/// <remarks>
/// Events carry everything the reducer needs, including the time they happened and the ids of
/// any messages they create, so the same sequence always produces the same state.
/// </remarks>
public abstract record ChatEvent
{
    /// <summary>
    /// When the event happened, in UTC. Left at <c>default</c>, <see cref="ChatStateService"/>
    /// stamps it with the current time as it applies the event.
    /// </summary>
    public DateTime Timestamp { get; init; }
}

/// <summary>Creates the stable ids chat messages are keyed by.</summary>
public static class ChatIds
{
    public static string New() => Guid.NewGuid().ToString("N");
}

/// <summary>How a turn the user started ended, as far as the front end could tell.</summary>
public enum TurnOutcome
{
    /// <summary>The agent's final reply arrived.</summary>
    Completed,

    /// <summary>No reply arrived in time. The agent may still be working; its reply can still arrive later.</summary>
    TimedOut,

    /// <summary>Sending, or waiting for the reply, failed.</summary>
    Failed,

    /// <summary>
    /// The turn was cancelled: by the user (who already stopped waiting), or by the agent, which
    /// then shows a notice when the turn is still the one this view is waiting on.
    /// </summary>
    Cancelled,

    /// <summary>The agent started the run, then went silent for longer than its heartbeat allows.</summary>
    AgentLost,

    /// <summary>The agent reported that the run failed. <see cref="TurnEnded.Reply"/> carries any error reply it sent.</summary>
    AgentError,
}

// ── Agent and conversation ──────────────────────────────────────────────────

/// <summary>The agent's display name and version became known or changed.</summary>
public sealed record AgentInfoReceived(string Name, string Version) : ChatEvent;

/// <summary>The stored conversation was loaded, replacing the transcript.</summary>
public sealed record HistoryLoaded(IReadOnlyList<ConversationHistoryTurn> Turns, string SessionId) : ChatEvent
{
    /// <summary>Prefix for the ids of the messages rebuilt from <see cref="Turns"/>.</summary>
    public string IdPrefix { get; init; } = ChatIds.New();
}

/// <summary>The conversation was cleared (<c>/clear</c>).</summary>
public sealed record ConversationCleared : ChatEvent;

// ── Turns the user starts ───────────────────────────────────────────────────

/// <summary>The user sent a message.</summary>
public sealed record UserMessageSent(
    string Content,
    string UserId,
    string SessionId,
    IReadOnlyList<AgentAttachment>? Attachments = null) : ChatEvent
{
    public string MessageId { get; init; } = ChatIds.New();
}

/// <summary>A turn started: the agent is working on what the user sent.</summary>
public sealed record TurnStarted(string TurnId) : ChatEvent;

/// <summary>Progress the agent reported on the turn it's working on.</summary>
public sealed record TurnProgress(string TurnId, string Content) : ChatEvent
{
    /// <summary>Id for the activity log, if this event starts one.</summary>
    public string MessageId { get; init; } = ChatIds.New();
}

/// <summary>
/// A turn ended. <see cref="Reply"/> is the agent's final reply for <see cref="TurnOutcome.Completed"/>;
/// <see cref="Error"/> says what went wrong for <see cref="TurnOutcome.Failed"/>.
/// </summary>
public sealed record TurnEnded(string TurnId, TurnOutcome Outcome) : ChatEvent
{
    public AgentReply? Reply { get; init; }
    public string? Error { get; init; }

    /// <summary>Id for the reply or error message this event adds.</summary>
    public string MessageId { get; init; } = ChatIds.New();
}

/// <summary>The user cancelled: stop waiting on the turn and on any form answers.</summary>
public sealed record UserCancelled : ChatEvent;

/// <summary>
/// The agent is still working on a run (a <see cref="RunHeartbeat"/>, or the run starting).
/// <see cref="Status"/> becomes the thinking text. Ignored when a different turn is running.
/// </summary>
public sealed record TurnHeartbeat(string TurnId, string? Status) : ChatEvent;

/// <summary>
/// A run this view no longer waits on finished (its turn timed out or was lost). Clears the
/// thinking text a late heartbeat left behind, when no turn is running.
/// </summary>
public sealed record LateRunFinished(string TurnId, string Outcome) : ChatEvent;

/// <summary>
/// A tool call started, finished or failed. Added to the source's activity log; an entry for
/// the same <see cref="ToolCallInfo.ToolCallId"/> is updated in place rather than appended.
/// </summary>
/// <param name="Content">The one-line text for the call, used as the entry and summary text.</param>
public sealed record ToolCallReported(
    MessageCategory Category,
    string? AgentName,
    ToolCallInfo ToolCall,
    string Content) : ChatEvent
{
    /// <summary>Id for the activity log, if this event starts one.</summary>
    public string MessageId { get; init; } = ChatIds.New();
}

/// <summary>A subagent or worker started: open its activity log.</summary>
public sealed record SubagentRunStarted(SubagentStarted Started) : ChatEvent
{
    /// <summary>Id for the activity log, if this event starts one.</summary>
    public string MessageId { get; init; } = ChatIds.New();
}

/// <summary>A subagent or worker finished: close its activity log and drop its task list.</summary>
public sealed record SubagentRunFinished(SubagentFinished Finished) : ChatEvent
{
    /// <summary>Id for the activity log, if this event has to start one to record the outcome.</summary>
    public string MessageId { get; init; } = ChatIds.New();
}

/// <summary>A task list was created or replaced.</summary>
public sealed record TaskListSnapshotReceived(TaskListSnapshot Snapshot) : ChatEvent;

/// <summary>One item of a task list changed.</summary>
public sealed record TaskListDeltaReceived(TaskListDelta Delta) : ChatEvent;

// ── Agent output outside a turn ─────────────────────────────────────────────

/// <summary>
/// Progress from some source (the primary agent, a subagent, an A2A task, a background task),
/// added to that source's activity log. <see cref="IsCompletion"/> closes the log.
/// </summary>
public sealed record ActivityReported(
    MessageCategory Category,
    string? AgentName,
    string Content,
    bool IsCompletion = false) : ChatEvent
{
    /// <summary>Id for the activity log, if this event starts one.</summary>
    public string MessageId { get; init; } = ChatIds.New();
}

/// <summary>A reply that arrived on its own rather than as the answer to a turn this front end is waiting on.</summary>
public sealed record ReplyReceived(AgentReply Reply, MessageCategory Category) : ChatEvent
{
    public string MessageId { get; init; } = ChatIds.New();
}

/// <summary>An error to show in the conversation.</summary>
public sealed record ErrorReported(string Message) : ChatEvent
{
    public string MessageId { get; init; } = ChatIds.New();
}

/// <summary>
/// The agent's own account of what's running, used to correct the front end's view after a
/// reload or reconnect (or the empty snapshot an agent broadcasts when it starts). Uses
/// <see cref="ActiveStatusResponse.Work"/> when present, so running workers are kept too.
/// </summary>
public sealed record ActiveStatusReconciled(ActiveStatusResponse Status) : ChatEvent
{
    /// <summary>Prefix for the ids of activity logs this event adds for running subagents and workers.</summary>
    public string IdPrefix { get; init; } = ChatIds.New();
}

// ── MCP questions ───────────────────────────────────────────────────────────

/// <summary>
/// A handed-back MCP question. <see cref="Form"/> is null when the form couldn't be read; the
/// message then shows the reply's plain text.
/// </summary>
public sealed record McpQuestionReceived(AgentReply Reply, McpQuestionForm? Form) : ChatEvent
{
    public string MessageId { get; init; } = ChatIds.New();
}

/// <summary>The bridge reported a question's state.</summary>
public sealed record McpQuestionStatusChanged(McpQuestionStatus Status) : ChatEvent;

/// <summary>
/// The user sent an answer (or a decline) for a question. Not accepted when the question is
/// unknown, closed, expired or already being sent.
/// </summary>
public sealed record McpAnswerSubmitted(string QuestionId) : ChatEvent;

/// <summary>An answer couldn't be sent; the form opens again.</summary>
public sealed record McpAnswerFailed(string QuestionId, string Reason) : ChatEvent;

// ── User actions on the transcript ──────────────────────────────────────────

/// <summary>The user expanded or collapsed a message.</summary>
public sealed record MessageExpandToggled(string MessageId) : ChatEvent;

/// <summary>The user rated a message. Not accepted when it's already rated.</summary>
public sealed record FeedbackRecorded(string MessageId, bool IsPositive) : ChatEvent;

/// <summary>The user put a saved response into the conversation as a reference.</summary>
public sealed record SavedReferenceAdded(
    string SavedResponseId,
    string Label,
    string Content,
    string AgentName) : ChatEvent
{
    public string MessageId { get; init; } = ChatIds.New();
}

/// <summary>The user dismissed a saved reference.</summary>
public sealed record SavedReferenceRemoved(string SavedResponseId) : ChatEvent;
