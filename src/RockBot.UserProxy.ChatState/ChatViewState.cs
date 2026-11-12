using System.Collections.Immutable;

namespace RockBot.UserProxy.ChatState;

public enum MessageCategory
{
    UserInput,
    PrimaryFinal,
    PrimaryProgress,
    SubagentActivity,
    A2AActivity,
    ScheduledSystem,
    ScheduledUser,
    Error,
    SavedReference,

    /// <summary>A handed-back MCP question, rendered as a form the user answers.</summary>
    McpQuestion,
}

public enum FeedbackState { None, ThumbsUp, ThumbsDown }

public sealed record ActivityLogEntry(string Content, DateTime Timestamp)
{
    /// <summary>
    /// The structured tool call this entry shows, when it is one. Its started, finished and
    /// failed reports share the entry, which always holds the latest of them.
    /// </summary>
    public ToolCallInfo? ToolCall { get; init; }
}

public sealed record ActiveStatusIndicator(MessageCategory Category, string? AgentName, string LatestContent);

/// <summary>
/// A run's task list as the agent last reported it.
/// </summary>
/// <param name="Key">The key it is stored under in <see cref="ChatViewState.TaskLists"/>.</param>
/// <param name="Source">Whose list it is: the primary turn, or a subagent (with its task id).</param>
/// <param name="RunId">The primary turn it belongs to, when it belongs to one.</param>
public sealed record TaskListView(
    string Key,
    ReplySource Source,
    string? RunId,
    ImmutableList<TaskListItem> Items,
    DateTime UpdatedAt)
{
    public int CompletedCount => Items.Count(i => i.Status == TaskListStatus.Completed);

    /// <summary>The item being worked on, else the first one not done yet.</summary>
    public TaskListItem? CurrentItem =>
        Items.FirstOrDefault(i => i.Status == TaskListStatus.InProgress)
        ?? Items.FirstOrDefault(i => i.Status != TaskListStatus.Completed);
}

/// <summary>Statuses a <see cref="TaskListItem"/> reports.</summary>
public static class TaskListStatus
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";

    /// <summary>The <see cref="ChatViewState.TaskLists"/> key for a source: <c>primary</c>, or the subagent's task id.</summary>
    public static string KeyFor(ReplySource source) =>
        source.Kind is ReplySourceKind.Subagent or ReplySourceKind.Worker && !string.IsNullOrEmpty(source.TaskId)
            ? source.TaskId!
            : ReplySourceKind.Primary;
}

/// <summary>A turn the agent is working on.</summary>
/// <param name="TurnId">
/// The id the front end gave the turn, or <see cref="ChatViewState.ReconciledTurnId"/> for one it
/// learned about from the agent's active status.
/// </param>
public sealed record TurnState(string TurnId, DateTime StartedAt);

/// <summary>
/// A chat as a front end shows it. Immutable: <see cref="ChatReducer"/> produces a new state for
/// each event, so a reader can render from one snapshot without locking.
/// </summary>
public sealed record ChatViewState
{
    public static ChatViewState Empty { get; } = new();

    /// <summary>
    /// How long a form answer counts as busy without its reply: long enough for the resumed call
    /// and a model turn, short enough that a lost reply can't disable the input for good.
    /// </summary>
    public static readonly TimeSpan FormWaitLimit = TimeSpan.FromMinutes(15);

    /// <summary>The <see cref="TurnState.TurnId"/> of a turn adopted from the agent's active status.</summary>
    public const string ReconciledTurnId = "agent-reported";

    public string? AgentName { get; init; }
    public string? AgentVersion { get; init; }

    /// <summary>The transcript, in order, each message keyed by <see cref="ChatMessage.MessageId"/>.</summary>
    public ImmutableList<ChatMessage> Messages { get; init; } = [];

    /// <summary>
    /// Activity logs still receiving entries: source key (category + agent name) to message id.
    /// Several sources can be open at once (the primary agent and a subagent, say).
    /// </summary>
    public ImmutableDictionary<string, string> OpenActivityLogs { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>The turn the agent is working on, if any.</summary>
    public TurnState? CurrentTurn { get; init; }

    public string? ThinkingMessage { get; init; }

    /// <summary>
    /// Task lists ("plans") the agent reported, keyed by source: <c>primary</c> for the user's
    /// turn, or a subagent's task id (<see cref="TaskListStatus.KeyFor"/>).
    /// </summary>
    public ImmutableDictionary<string, TaskListView> TaskLists { get; init; } =
        ImmutableDictionary.Create<string, TaskListView>(StringComparer.Ordinal);

    /// <summary>
    /// Form answers sent and still waiting for the agent's follow-up reply, by question id, with
    /// when they were sent. Kept apart from <see cref="CurrentTurn"/>: a form can be answered
    /// while the turn that showed it is still running, and that turn's final reply must not end
    /// the wait for the follow-up.
    /// </summary>
    public ImmutableDictionary<string, DateTime> FormWaits { get; init; } =
        ImmutableDictionary.Create<string, DateTime>(StringComparer.Ordinal);

    /// <summary>True while a turn is running, or a form answer is waiting for the agent's follow-up.</summary>
    public bool IsBusy(DateTime utcNow) =>
        CurrentTurn is not null || FormWaits.Values.Any(sent => utcNow - sent < FormWaitLimit);

    /// <summary>True if <paramref name="messageId"/> is an activity log still receiving entries.</summary>
    public bool IsActivityLogOpen(string messageId) => OpenActivityLogs.ContainsValue(messageId);

    /// <summary>True if any activity log in the transcript is still receiving entries.</summary>
    public bool HasOpenActivityLog =>
        Messages.Any(m => m.IsActivityLog && IsActivityLogOpen(m.MessageId));

    /// <summary>
    /// Running subagent and background-task indicators for the header, in transcript order.
    /// Agent-busy state is separate: see <see cref="IsBusy"/>.
    /// </summary>
    public IReadOnlyList<ActiveStatusIndicator> ActiveStatusIndicators =>
        Messages
            .Where(m => m.Category is MessageCategory.SubagentActivity or MessageCategory.ScheduledSystem
                        && IsActivityLogOpen(m.MessageId))
            .Select(m => new ActiveStatusIndicator(m.Category, m.AgentName, m.Content))
            .ToList();

    /// <summary>The most recent saved reference, which the next user message carries as context.</summary>
    public ChatMessage? ActiveSavedReference =>
        Messages.LastOrDefault(m => m.Category == MessageCategory.SavedReference);

    public ChatMessage? FindMessage(string messageId) =>
        Messages.FirstOrDefault(m => m.MessageId == messageId);

    public ChatMessage? FindQuestion(string questionId) =>
        Messages.FirstOrDefault(m => m.Question?.QuestionId == questionId);
}

public sealed record ChatMessage
{
    public string MessageId { get; init; } = ChatIds.New();
    public required string Content { get; init; }
    public required bool IsFromUser { get; init; }
    public required DateTime Timestamp { get; init; }
    public string? AgentName { get; init; }
    public string? UserId { get; init; }
    public string? SessionId { get; init; }
    public string? ContentType { get; init; }

    /// <summary>
    /// Out-of-band attachments (images, etc.) carried on a final agent reply. Rendered natively
    /// (outside the markdown sanitizer) as &lt;img&gt; for image MIME types or a download link
    /// otherwise. Null for text-only replies.
    /// </summary>
    public IReadOnlyList<AgentAttachment>? Attachments { get; init; }

    public bool IsError { get; init; }

    /// <summary>
    /// True for progress/interim messages (IsFinal=false) — subagent status updates,
    /// A2A working state, and raw completion previews before primary synthesis.
    /// Rendered with a muted style to distinguish from final primary-agent replies.
    /// </summary>
    public bool IsInterim { get; init; }

    /// <summary>Visibility category used by the UI to determine default collapse state and styling.</summary>
    public MessageCategory Category { get; init; } = MessageCategory.PrimaryFinal;

    /// <summary>True for the grouped WIP activity log bubble that accumulates progress entries.</summary>
    public bool IsActivityLog { get; init; }

    /// <summary>Accumulated progress entries for activity-log bubbles.</summary>
    public ImmutableList<ActivityLogEntry> ActivityLogEntries { get; init; } = [];

    /// <summary>Expanded/collapsed state for the UI toggle.</summary>
    public bool IsExpanded { get; init; } = true;

    public FeedbackState Feedback { get; init; } = FeedbackState.None;

    /// <summary>ID of the saved response this message represents, if Category == SavedReference.</summary>
    public string? SavedResponseId { get; init; }

    /// <summary>Label of the saved response, shown as a badge.</summary>
    public string? SavedResponseLabel { get; init; }

    /// <summary>
    /// The handed-back MCP question this bubble shows as a form, if Category == McpQuestion.
    /// Null when the form couldn't be read; the bubble then shows <see cref="Content"/>.
    /// </summary>
    public McpQuestionForm? Question { get; init; }

    /// <summary>The question's last reported <see cref="McpQuestionState"/>; null while it's open.</summary>
    public string? QuestionState { get; init; }

    /// <summary>Why the question is in <see cref="QuestionState"/>, when the bridge said.</summary>
    public string? QuestionReason { get; init; }

    /// <summary>Who settled the question (<c>user</c> or <c>agent</c>), when it was answered or declined.</summary>
    public string? QuestionAnsweredBy { get; init; }

    /// <summary>True between sending the form's answer and hearing back.</summary>
    public bool IsSubmitting { get; init; }

    /// <summary>True while the question can still be answered: not settled and not past its expiry.</summary>
    public bool IsQuestionOpen(DateTimeOffset now) =>
        Question is not null && !McpQuestionState.IsClosed(QuestionState) && now < Question.ExpiresAt;
}
