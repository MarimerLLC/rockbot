using RockBot.UserProxy;

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

    /// <summary>A handed-back MCP question, rendered as a form the user answers.</summary>
    McpQuestion,
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
    /// Form answers sent and still waiting for the agent's follow-up reply, by question id, with
    /// when they were sent. Kept apart from <see cref="_isProcessing"/>: a form can be answered
    /// while the turn that showed it is still running, and that turn's final reply must not end
    /// the wait for the follow-up. Mutated under <see cref="_lock"/>.
    /// </summary>
    private readonly Dictionary<string, DateTime> _formWaits = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a form answer counts as busy without its reply: long enough for the resumed call
    /// and a model turn, short enough that a lost reply can't disable the input for good.
    /// </summary>
    internal static readonly TimeSpan FormWaitLimit = TimeSpan.FromMinutes(15);

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
    /// <summary>True while a turn is running, or a form answer is waiting for the agent's follow-up.</summary>
    public bool IsProcessing
    {
        get
        {
            if (_isProcessing)
                return true;
            lock (_lock)
                return _formWaits.Values.Any(sent => DateTime.UtcNow - sent < FormWaitLimit);
        }
    }

    public void LoadHistory(IReadOnlyList<ConversationHistoryTurn> turns, string sessionId)
    {
        lock (_lock)
        {
            // An open MCP question isn't part of the conversation history (it lives in the
            // bridge), so keep its form across the rebuild; otherwise a reload would leave the
            // agent's "fill in the form" with no form to fill in.
            var openQuestions = _messages
                .Where(m => m.Category == MessageCategory.McpQuestion && m.IsQuestionOpen(DateTimeOffset.UtcNow))
                .ToList();

            _messages.Clear();

            // The bubbles those entries point at were just discarded, so anything left
            // here is a dangling reference to a message that no longer exists. This
            // service is a singleton that outlives the circuit, so without the reset a
            // reload mid-turn carries the previous circuit's spinner state into the
            // restored transcript.
            _activeActivityLogs.Clear();
            _currentThinkingMessage = null;

            foreach (var turn in turns)
            {
                // Skip system/consolidation turns — they are internal prompts not meant for the user
                if (turn.Role == "system")
                    continue;

                var (category, isFromUser) = CategorizeHistoryTurn(turn, AgentName);

                // Use the current display name for primary agent turns so
                // history reflects a name change (e.g. "RockBot" → "DockerBot").
                var effectiveName = category is MessageCategory.PrimaryFinal or MessageCategory.PrimaryProgress
                    ? AgentName ?? turn.AgentName
                    : turn.AgentName;

                _messages.Add(new ChatMessage
                {
                    Content = turn.Content,
                    IsFromUser = isFromUser,
                    Timestamp = turn.Timestamp.UtcDateTime,
                    SessionId = sessionId,
                    AgentName = effectiveName,
                    Category = category,
                    Attachments = turn.Attachments,
                    IsExpanded = category is MessageCategory.PrimaryFinal
                        or MessageCategory.ScheduledUser
                        or MessageCategory.UserInput
                        or MessageCategory.Error
                });
            }

            foreach (var question in openQuestions)
            {
                var index = _messages.FindIndex(m => m.Timestamp > question.Timestamp);
                _messages.Insert(index < 0 ? _messages.Count : index, question);
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
            // Subagent and worker synthetic turns are stored as "user" role with a
            // subagent- or worker- prefixed AgentName. Both represent spawned activity.
            if (turn.AgentName?.StartsWith("subagent-", StringComparison.OrdinalIgnoreCase) == true
                || turn.AgentName?.StartsWith("worker-", StringComparison.OrdinalIgnoreCase) == true)
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

    public void AddUserMessage(
        string content, string userId, string sessionId,
        IReadOnlyList<AgentAttachment>? attachments = null)
    {
        lock (_lock)
            _messages.Add(new ChatMessage
            {
                Content = content,
                IsFromUser = true,
                Timestamp = DateTime.UtcNow,
                UserId = userId,
                SessionId = sessionId,
                Category = MessageCategory.UserInput,
                Attachments = attachments
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
        if (category == MessageCategory.McpQuestion)
        {
            AddMcpQuestion(reply);
            return;
        }

        lock (_lock)
        {
            // The follow-up to a form answer ends that answer's wait (and only that one).
            if (reply.FollowsQuestionId is { } answered)
                _formWaits.Remove(answered);

            // Close the activity log for the source that just finished.
            // PrimaryFinal closes the PrimaryProgress log (different category names);
            // subagent / A2A / scheduled final replies close their own category's log.
            if (category == MessageCategory.PrimaryFinal)
            {
                _activeActivityLogs.Remove(ActivityLogKey(MessageCategory.PrimaryProgress, null));

                // The primary agent's turn is over, whoever was waiting on it. The send
                // loop clears this too, but only for the circuit that started the turn —
                // if that circuit is gone (reload, SignalR reconnect) its continuation
                // never runs and the busy state would stick on this singleton forever,
                // leaving a spinner under the reply and a disabled input box.
                _isProcessing = false;
            }
            else
                _activeActivityLogs.Remove(ActivityLogKey(category, reply.AgentName));

            _messages.Add(new ChatMessage
            {
                Content = reply.Content,
                IsFromUser = false,
                Timestamp = DateTime.UtcNow,
                AgentName = reply.AgentName,
                SessionId = reply.SessionId,
                ContentType = reply.ContentType,
                Attachments = reply.Attachments,
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
    /// Adds a handed-back MCP question as a form bubble. It arrives in the middle of the agent's
    /// turn (the agent is still writing its reply), so unlike a final reply it leaves the busy
    /// state and the activity log alone. A reply whose form can't be read shows its plain-text
    /// <see cref="AgentReply.Content"/> instead.
    /// </summary>
    private void AddMcpQuestion(AgentReply reply)
    {
        var form = McpQuestionFormModel.TryParseForm(reply.StructuredData);
        lock (_lock)
        {
            if (form is not null && _messages.Any(m => m.Question?.QuestionId == form.QuestionId))
                return;

            _messages.Add(new ChatMessage
            {
                Content = reply.Content,
                IsFromUser = false,
                Timestamp = DateTime.UtcNow,
                AgentName = reply.AgentName,
                SessionId = reply.SessionId,
                ContentType = reply.ContentType,
                Category = MessageCategory.McpQuestion,
                Question = form,
                IsExpanded = true,
            });
        }
        NotifyStateChanged();
    }

    /// <summary>
    /// Applies a state change to the form for <see cref="McpQuestionStatus.QuestionId"/>. A
    /// closed form stays closed: a late <c>rejected</c> can't reopen it. Unknown ids are ignored.
    /// </summary>
    public void UpdateQuestionStatus(McpQuestionStatus status)
    {
        lock (_lock)
        {
            var msg = _messages.FirstOrDefault(m => m.Question?.QuestionId == status.QuestionId);
            if (msg is null || McpQuestionState.IsClosed(msg.QuestionState))
                return;

            // Only an accepted answer or decline leads to an agent reply; anything else ends this
            // form's wait here. A turn that's still running stays busy on its own account.
            if (status.State is not (McpQuestionState.Answered or McpQuestionState.Declined))
                _formWaits.Remove(status.QuestionId);

            msg.QuestionState = status.State;
            msg.QuestionReason = status.Reason;
            msg.QuestionAnsweredBy = status.AnsweredBy;
            msg.IsSubmitting = false;
        }
        NotifyStateChanged();
    }

    /// <summary>
    /// Marks the form for <paramref name="questionId"/> as sent. False when it can't be sent: it's
    /// unknown, closed, expired, or already being sent (another tab got there first).
    /// </summary>
    public bool TryBeginQuestionSubmit(string questionId)
    {
        lock (_lock)
        {
            var msg = _messages.FirstOrDefault(m => m.Question?.QuestionId == questionId);
            if (msg is null || msg.IsSubmitting || !msg.IsQuestionOpen(DateTimeOffset.UtcNow))
                return false;

            msg.IsSubmitting = true;
            _formWaits[questionId] = DateTime.UtcNow;
            msg.QuestionState = null;
            msg.QuestionReason = null;
        }
        NotifyStateChanged();
        return true;
    }

    /// <summary>Re-opens a form whose answer couldn't be sent.</summary>
    public void FailQuestionSubmit(string questionId, string reason) =>
        UpdateQuestionStatus(new McpQuestionStatus
        {
            SessionId = "",
            QuestionId = questionId,
            State = McpQuestionState.Rejected,
            Reason = reason,
        });

    /// <summary>
    /// Appends an entry to an activity log bubble for the given source.
    /// Creates the bubble if one doesn't exist yet. Multiple concurrent sources
    /// (primary, subagent-X, scheduled-system) each get their own activity log.
    /// </summary>
    public void AppendActivityLogEntry(
        string content,
        MessageCategory category = MessageCategory.PrimaryProgress,
        string? agentName = null,
        bool close = false)
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

            if (close)
                _activeActivityLogs.Remove(key);
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

    // ── Clear context ───────────────────────────────────────────────────────

    public void ClearMessages()
    {
        lock (_lock)
        {
            _messages.Clear();
            _activeActivityLogs.Clear();
            _formWaits.Clear();
        }
        _currentThinkingMessage = null;
        _isProcessing = false;
        NotifyStateChanged();
    }

    /// <summary>
    /// The user cancelled: stop waiting on everything, form answers included. A turn's own
    /// completion uses <see cref="SetProcessing"/>, which leaves form waits alone.
    /// </summary>
    public void CancelProcessing()
    {
        lock (_lock)
            _formWaits.Clear();
        SetProcessing(false);
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

    /// <summary>
    /// Reconciles local activity-log state against the authoritative snapshot from
    /// the agent. Removes stale entries for subagents that are no longer running and
    /// seeds entries for subagents that are active but unknown to the UI (e.g. after
    /// a page reload while subagents were in flight). The agent's own busy flag is
    /// adopted as well, so a reload lands on the real state rather than on whatever
    /// the previous circuit left behind.
    /// </summary>
    public void ReconcileActiveStatus(ActiveStatusResponse status)
    {
        lock (_lock)
        {
            _isProcessing = status.IsProcessing;

            var activeAgentNames = status.Subagents
                .Select(s => $"subagent-{s.TaskId}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Remove stale subagent activity logs for subagents that are no longer running
            var subagentPrefix = $"{MessageCategory.SubagentActivity}:";
            var keysToRemove = _activeActivityLogs.Keys
                .Where(k => k.StartsWith(subagentPrefix, StringComparison.Ordinal) &&
                            !activeAgentNames.Contains(k[subagentPrefix.Length..]))
                .ToList();

            foreach (var key in keysToRemove)
                _activeActivityLogs.Remove(key);

            // Seed activity log entries for subagents that are running but have no UI state
            foreach (var sub in status.Subagents)
            {
                var agentName = $"subagent-{sub.TaskId}";
                var key = ActivityLogKey(MessageCategory.SubagentActivity, agentName);

                if (!_activeActivityLogs.ContainsKey(key))
                {
                    var bubble = new ChatMessage
                    {
                        Content = $"Running: {sub.Description}",
                        IsFromUser = false,
                        Timestamp = sub.StartedAt.UtcDateTime,
                        AgentName = agentName,
                        Category = MessageCategory.SubagentActivity,
                        IsActivityLog = true,
                        IsExpanded = false
                    };
                    bubble.ActivityLogEntries.Add(new ActivityLogEntry(
                        $"Running: {sub.Description}", sub.StartedAt.UtcDateTime));
                    _messages.Add(bubble);
                    _activeActivityLogs[key] = bubble.MessageId;
                }
            }
        }
        NotifyStateChanged();
    }

    private static string ActivityLogKey(MessageCategory category, string? agentName)
        => category == MessageCategory.PrimaryProgress
            ? $"{category}:"
            : $"{category}:{agentName ?? ""}";

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

    /// <summary>
    /// The handed-back MCP question this bubble shows as a form, if Category == McpQuestion.
    /// Null when the form couldn't be read; the bubble then shows <see cref="Content"/>.
    /// </summary>
    public McpQuestionForm? Question { get; init; }

    /// <summary>
    /// The question's last reported <see cref="McpQuestionState"/>; null while it's open.
    /// Mutated under the service's lock.
    /// </summary>
    public string? QuestionState { get; set; }

    /// <summary>Why the question is in <see cref="QuestionState"/>, when the bridge said.</summary>
    public string? QuestionReason { get; set; }

    /// <summary>Who settled the question (<c>user</c> or <c>agent</c>), when it was answered or declined.</summary>
    public string? QuestionAnsweredBy { get; set; }

    /// <summary>True between sending the form's answer and hearing back.</summary>
    public bool IsSubmitting { get; set; }

    /// <summary>True while the question can still be answered: not settled and not past its expiry.</summary>
    public bool IsQuestionOpen(DateTimeOffset now) =>
        Question is not null && !McpQuestionState.IsClosed(QuestionState) && now < Question.ExpiresAt;
}
