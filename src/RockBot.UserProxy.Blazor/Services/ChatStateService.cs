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
    SavedReference,
}

public sealed record ActivityLogEntry(string Content, DateTime Timestamp);
public sealed record ActiveStatusIndicator(MessageCategory Category, string? AgentName, string LatestContent);

/// <summary>
/// Manages chat state and provides real-time updates to Blazor components.
/// </summary>
public sealed class ChatStateService
{
    private readonly List<ChatMessage> _messages = new();
    private readonly object _lock = new();
    private string? _currentThinkingMessage;
    private bool _isProcessing;

    /// <summary>
    /// Active activity log bubbles keyed by source key (category + agentName).
    /// Multiple sources can accumulate concurrently (e.g. primary + subagent).
    /// </summary>
    private readonly Dictionary<string, string> _activeActivityLogs = new();

    public string? AgentName { get; private set; }
    public string? AgentVersion { get; private set; }

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
                // Skip system/consolidation turns — they are internal prompts not meant for the user
                if (turn.Role == "system")
                    continue;

                var (category, isFromUser) = CategorizeHistoryTurn(turn, AgentName);

                _messages.Add(new ChatMessage
                {
                    Content = turn.Content,
                    IsFromUser = isFromUser,
                    Timestamp = turn.Timestamp.UtcDateTime,
                    SessionId = sessionId,
                    AgentName = turn.AgentName,
                    Category = category,
                    IsExpanded = category is MessageCategory.PrimaryFinal
                        or MessageCategory.ScheduledUser
                        or MessageCategory.UserInput
                        or MessageCategory.Error
                });
            }
        }
        NotifyStateChanged();
    }

    /// <summary>
    /// Determines the display category for a restored history turn based on its AgentName
    /// and content patterns.
    /// </summary>
    private static (MessageCategory Category, bool IsFromUser) CategorizeHistoryTurn(
        ConversationHistoryTurn turn, string? primaryAgentName)
    {
        if (turn.Role == "user")
        {
            // Subagent synthetic turns are stored as "user" role with a subagent AgentName
            if (turn.AgentName?.StartsWith("subagent-", StringComparison.OrdinalIgnoreCase) == true)
                return (MessageCategory.SubagentActivity, false);

            // A2A synthetic turns have a non-null AgentName that differs from the primary agent
            if (!string.IsNullOrEmpty(turn.AgentName) &&
                !string.Equals(turn.AgentName, primaryAgentName, StringComparison.OrdinalIgnoreCase))
                return (MessageCategory.A2AActivity, false);

            return (MessageCategory.UserInput, true);
        }

        // Assistant turns
        return (MessageCategory.PrimaryFinal, false);
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

    public void SetAgentInfo(string name, string version)
    {
        AgentName = name;
        AgentVersion = version;
        NotifyStateChanged();
    }

    public void AddAgentReply(AgentReply reply, MessageCategory category = MessageCategory.PrimaryFinal)
    {
        lock (_lock)
        {
            // When a PrimaryFinal message arrives, close the primary activity log
            if (category == MessageCategory.PrimaryFinal)
                _activeActivityLogs.Remove(ActivityLogKey(MessageCategory.PrimaryProgress, null));

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
    /// Appends an entry to an activity log bubble for the given source.
    /// Creates the bubble if one doesn't exist yet. Multiple concurrent sources
    /// (primary, subagent-X, scheduled-system) each get their own activity log.
    /// </summary>
    public void AppendActivityLogEntry(
        string content,
        MessageCategory category = MessageCategory.PrimaryProgress,
        string? agentName = null)
    {
        lock (_lock)
        {
            var key = ActivityLogKey(category, agentName);

            ChatMessage? logBubble = null;
            if (_activeActivityLogs.TryGetValue(key, out var logId))
                logBubble = _messages.FirstOrDefault(m => m.MessageId == logId);

            if (logBubble is null)
            {
                logBubble = new ChatMessage
                {
                    Content = content,
                    IsFromUser = false,
                    Timestamp = DateTime.UtcNow,
                    AgentName = agentName,
                    Category = category,
                    IsActivityLog = true,
                    IsExpanded = false
                };
                _messages.Add(logBubble);
                _activeActivityLogs[key] = logBubble.MessageId;
            }

            logBubble.ActivityLogEntries.Add(new ActivityLogEntry(content, DateTime.UtcNow));
            logBubble.Content = content; // summary line = latest entry
        }
        NotifyStateChanged();
    }

    /// <summary>
    /// Returns true if the given message ID is a currently active (still receiving entries) activity log.
    /// </summary>
    public bool IsActiveActivityLog(string messageId)
    {
        lock (_lock)
        {
            return _activeActivityLogs.ContainsValue(messageId);
        }
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
            _activeActivityLogs.Clear();
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

    // ── Saved references ────────────────────────────────────────────────────

    public void AddSavedReference(string id, string label, string content, string agentName)
    {
        lock (_lock)
            _messages.Add(new ChatMessage
            {
                Content = content,
                IsFromUser = false,
                Timestamp = DateTime.UtcNow,
                AgentName = agentName,
                Category = MessageCategory.SavedReference,
                SavedResponseId = id,
                SavedResponseLabel = label,
                IsExpanded = true
            });
        NotifyStateChanged();
    }

    public void RemoveSavedReference(string savedResponseId)
    {
        lock (_lock)
            _messages.RemoveAll(m => m.Category == MessageCategory.SavedReference && m.SavedResponseId == savedResponseId);
        NotifyStateChanged();
    }

    /// <summary>
    /// Returns the most recent saved reference in the message list, or null if none is active.
    /// Used by SendMessage to inject context into the user's message.
    /// </summary>
    public ChatMessage? GetActiveSavedReference()
    {
        lock (_lock)
            return _messages.LastOrDefault(m => m.Category == MessageCategory.SavedReference);
    }

    /// <summary>
    /// Returns active subagent and background-task indicators for header display.
    /// Agent-busy state is tracked separately via <see cref="IsProcessing"/>.
    /// </summary>
    public IReadOnlyList<ActiveStatusIndicator> GetActiveStatusIndicators()
    {
        lock (_lock)
        {
            var indicators = new List<ActiveStatusIndicator>();
            foreach (var (_, messageId) in _activeActivityLogs)
            {
                var msg = _messages.FirstOrDefault(m => m.MessageId == messageId);
                if (msg is null) continue;
                if (msg.Category is MessageCategory.SubagentActivity or MessageCategory.ScheduledSystem)
                    indicators.Add(new ActiveStatusIndicator(msg.Category, msg.AgentName, msg.Content));
            }
            return indicators;
        }
    }

    private static string ActivityLogKey(MessageCategory category, string? agentName)
        => $"{category}:{agentName ?? ""}";

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

    /// <summary>ID of the saved response this message represents, if Category == SavedReference.</summary>
    public string? SavedResponseId { get; init; }

    /// <summary>Label of the saved response, shown as a badge.</summary>
    public string? SavedResponseLabel { get; init; }
}
