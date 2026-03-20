namespace RockBot.UserProxy.Blazor.Services;

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
}

public sealed record ActivityLogEntry(string Content, DateTime Timestamp);

/// <summary>
/// Manages chat state and provides real-time updates to Blazor components.
/// </summary>
public sealed class ChatStateService
{
    private readonly List<ChatMessage> _messages = new();
    private readonly object _lock = new();
    private string? _currentThinkingMessage;
    private bool _isProcessing;
    private string? _activeActivityLogId;

    public event Action? OnStateChanged;

    /// <summary>
    /// Returns a point-in-time snapshot of the message list.
    /// Callers (including Blazor's render loop) iterate the snapshot, so concurrent
    /// mutations on background threads cannot cause "Collection was modified" exceptions.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (_lock) return _messages.ToList(); }
    }

    public string? CurrentThinkingMessage => _currentThinkingMessage;
    public bool IsProcessing => _isProcessing;

    public void LoadHistory(IReadOnlyList<ConversationHistoryTurn> turns, string sessionId)
    {
        lock (_lock)
        {
            _messages.Clear();
            foreach (var turn in turns)
            {
                _messages.Add(new ChatMessage
                {
                    Content = turn.Content,
                    IsFromUser = turn.Role == "user",
                    Timestamp = turn.Timestamp.UtcDateTime,
                    SessionId = sessionId,
                    Category = turn.Role == "user" ? MessageCategory.UserInput : MessageCategory.PrimaryFinal
                });
            }
        }
        NotifyStateChanged();
    }

    public void AddUserMessage(string content, string userId, string sessionId)
    {
        lock (_lock)
            _messages.Add(new ChatMessage
            {
                Content = content,
                IsFromUser = true,
                Timestamp = DateTime.UtcNow,
                UserId = userId,
                SessionId = sessionId,
                Category = MessageCategory.UserInput
            });
        NotifyStateChanged();
    }

    public void AddAgentReply(AgentReply reply, MessageCategory category = MessageCategory.PrimaryFinal)
    {
        lock (_lock)
        {
            // When a PrimaryFinal message arrives, close any active activity log
            if (category == MessageCategory.PrimaryFinal)
                _activeActivityLogId = null;

            _messages.Add(new ChatMessage
            {
                Content = reply.Content,
                IsFromUser = false,
                Timestamp = DateTime.UtcNow,
                AgentName = reply.AgentName,
                SessionId = reply.SessionId,
                ContentType = reply.ContentType,
                IsInterim = !reply.IsFinal,
                Category = category,
                IsExpanded = category is MessageCategory.PrimaryFinal
                    or MessageCategory.ScheduledUser
                    or MessageCategory.UserInput
                    or MessageCategory.Error
            });
        }
        NotifyStateChanged();
    }

    /// <summary>
    /// Appends an entry to the active WIP activity log bubble. Creates the bubble
    /// if one doesn't exist yet for the current processing cycle.
    /// </summary>
    public void AppendActivityLogEntry(string content)
    {
        lock (_lock)
        {
            ChatMessage? logBubble = null;
            if (_activeActivityLogId is not null)
                logBubble = _messages.FirstOrDefault(m => m.MessageId == _activeActivityLogId);

            if (logBubble is null)
            {
                logBubble = new ChatMessage
                {
                    Content = content,
                    IsFromUser = false,
                    Timestamp = DateTime.UtcNow,
                    Category = MessageCategory.PrimaryProgress,
                    IsActivityLog = true,
                    IsExpanded = false
                };
                _messages.Add(logBubble);
                _activeActivityLogId = logBubble.MessageId;
            }

            logBubble.ActivityLogEntries.Add(new ActivityLogEntry(content, DateTime.UtcNow));
            logBubble.Content = content; // summary line = latest entry
        }
        NotifyStateChanged();
    }

    /// <summary>
    /// Toggles the expanded/collapsed state of a message by its ID.
    /// </summary>
    public void ToggleExpanded(string messageId)
    {
        lock (_lock)
        {
            var msg = _messages.FirstOrDefault(m => m.MessageId == messageId);
            if (msg is not null)
                msg.IsExpanded = !msg.IsExpanded;
        }
        NotifyStateChanged();
    }

    /// <summary>
    /// Records the user's thumbs-up or thumbs-down on a specific agent message.
    /// Has no effect if the message has already been rated.
    /// </summary>
    public void RecordFeedback(string messageId, bool isPositive)
    {
        lock (_lock)
        {
            var msg = _messages.FirstOrDefault(m => m.MessageId == messageId);
            if (msg is null || msg.Feedback != FeedbackState.None)
                return;
            msg.Feedback = isPositive ? FeedbackState.ThumbsUp : FeedbackState.ThumbsDown;
        }
        NotifyStateChanged();
    }

    public void SetThinkingMessage(string? message)
    {
        _currentThinkingMessage = message;
        NotifyStateChanged();
    }

    public void SetProcessing(bool isProcessing)
    {
        _isProcessing = isProcessing;
        if (isProcessing)
        {
            _currentThinkingMessage = null;
            _activeActivityLogId = null;
        }
        NotifyStateChanged();
    }

    public void AddError(string message)
    {
        lock (_lock)
            _messages.Add(new ChatMessage
            {
                Content = message,
                IsFromUser = false,
                IsError = true,
                Timestamp = DateTime.UtcNow,
                Category = MessageCategory.Error,
                IsExpanded = true
            });
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}

public enum FeedbackState { None, ThumbsUp, ThumbsDown }

public sealed class ChatMessage
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");
    public required string Content { get; set; }
    public required bool IsFromUser { get; init; }
    public required DateTime Timestamp { get; init; }
    public string? AgentName { get; init; }
    public string? UserId { get; init; }
    public string? SessionId { get; init; }
    public string? ContentType { get; init; }
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

    /// <summary>Accumulated progress entries for activity-log bubbles. Thread-safe: mutated under _lock.</summary>
    public List<ActivityLogEntry> ActivityLogEntries { get; } = new();

    /// <summary>Mutable expanded/collapsed state for the UI toggle.</summary>
    public bool IsExpanded { get; set; } = true;

    /// <summary>Mutable so <see cref="ChatStateService.RecordFeedback"/> can update it in place.</summary>
    public FeedbackState Feedback { get; set; } = FeedbackState.None;
}
