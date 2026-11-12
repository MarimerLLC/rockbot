using System.Collections.Immutable;

namespace RockBot.UserProxy.ChatState;

/// <summary>The state after an event, and whether the event was accepted.</summary>
/// <param name="State">The new state; the same instance when the event changed nothing.</param>
/// <param name="Accepted">
/// False when the event was refused or had nothing to act on, for example an answer to a form
/// that's already closed, or feedback on a message that's already rated.
/// </param>
public readonly record struct ChatReduction(ChatViewState State, bool Accepted);

/// <summary>
/// Folds <see cref="ChatEvent"/>s into a <see cref="ChatViewState"/>. Pure: the result depends
/// only on the state and the event, so a recorded sequence always replays to the same state.
/// Each event type has its own reducer below.
/// </summary>
public static class ChatReducer
{
    public static ChatViewState Reduce(ChatViewState state, ChatEvent e) => Apply(state, e).State;

    /// <summary>Applies <paramref name="events"/> in order, starting from <paramref name="initial"/> or an empty chat.</summary>
    public static ChatViewState Replay(IEnumerable<ChatEvent> events, ChatViewState? initial = null) =>
        events.Aggregate(initial ?? ChatViewState.Empty, Reduce);

    public static ChatReduction Apply(ChatViewState state, ChatEvent e) => e switch
    {
        AgentInfoReceived x => Accept(state with { AgentName = x.Name, AgentVersion = x.Version }),
        HistoryLoaded x => Accept(OnHistoryLoaded(state, x)),
        ConversationCleared => Accept(ChatViewState.Empty with
        {
            AgentName = state.AgentName,
            AgentVersion = state.AgentVersion,
        }),
        UserMessageSent x => Accept(OnUserMessageSent(state, x)),
        TurnStarted x => Accept(OnTurnStarted(state, x)),
        TurnProgress x => Accept(AppendActivity(state, MessageCategory.PrimaryProgress, null, x.Content,
            close: false, x.MessageId, x.Timestamp)),
        TurnEnded x => Accept(OnTurnEnded(state, x)),
        UserCancelled => Accept(state with
        {
            CurrentTurn = null,
            FormWaits = state.FormWaits.Clear(),
        }),
        ActivityReported x => Accept(AppendActivity(state, x.Category, x.AgentName, x.Content,
            x.IsCompletion, x.MessageId, x.Timestamp)),
        ReplyReceived x => Accept(AddReply(state with { ThinkingMessage = null }, x.Reply, x.Category,
            x.MessageId, x.Timestamp)),
        ErrorReported x => Accept(AddError(state, x.Message, x.MessageId, x.Timestamp)),
        ActiveStatusReconciled x => Accept(OnActiveStatusReconciled(state, x)),
        McpQuestionReceived x => OnMcpQuestionReceived(state, x),
        McpQuestionStatusChanged x => OnMcpQuestionStatusChanged(state, x.Status),
        McpAnswerSubmitted x => OnMcpAnswerSubmitted(state, x),
        McpAnswerFailed x => OnMcpQuestionStatusChanged(state, new McpQuestionStatus
        {
            SessionId = "",
            QuestionId = x.QuestionId,
            State = McpQuestionState.Rejected,
            Reason = x.Reason,
        }),
        MessageExpandToggled x => UpdateMessage(state, x.MessageId, m => m with { IsExpanded = !m.IsExpanded }),
        FeedbackRecorded x => OnFeedbackRecorded(state, x),
        SavedReferenceAdded x => Accept(state with
        {
            Messages = state.Messages.Add(new ChatMessage
            {
                MessageId = x.MessageId,
                Content = x.Content,
                IsFromUser = false,
                Timestamp = x.Timestamp,
                AgentName = x.AgentName,
                Category = MessageCategory.SavedReference,
                SavedResponseId = x.SavedResponseId,
                SavedResponseLabel = x.Label,
                IsExpanded = true,
            }),
        }),
        SavedReferenceRemoved x => Accept(state with
        {
            Messages = state.Messages.RemoveAll(m =>
                m.Category == MessageCategory.SavedReference && m.SavedResponseId == x.SavedResponseId),
        }),
        _ => Reject(state),
    };

    // ── Conversation ────────────────────────────────────────────────────────

    private static ChatViewState OnHistoryLoaded(ChatViewState state, HistoryLoaded e)
    {
        // An open MCP question isn't part of the conversation history (it lives in the bridge),
        // so keep its form across the rebuild; otherwise a reload would leave the agent's "fill
        // in the form" with no form to fill in.
        var now = AsOffset(e.Timestamp);
        var openQuestions = state.Messages
            .Where(m => m.Category == MessageCategory.McpQuestion && m.IsQuestionOpen(now))
            .ToList();

        var messages = ImmutableList.CreateBuilder<ChatMessage>();
        var index = 0;
        foreach (var turn in e.Turns)
        {
            // Skip system/consolidation turns — they are internal prompts not meant for the user.
            if (turn.Role == "system")
                continue;

            var (category, isFromUser) = CategorizeHistoryTurn(turn, state.AgentName);

            // Use the current display name for primary agent turns so history reflects a name
            // change (e.g. "RockBot" → "DockerBot").
            var effectiveName = category is MessageCategory.PrimaryFinal or MessageCategory.PrimaryProgress
                ? state.AgentName ?? turn.AgentName
                : turn.AgentName;

            messages.Add(new ChatMessage
            {
                MessageId = $"{e.IdPrefix}-{index++}",
                Content = turn.Content,
                IsFromUser = isFromUser,
                Timestamp = turn.Timestamp.UtcDateTime,
                SessionId = e.SessionId,
                AgentName = effectiveName,
                Category = category,
                Attachments = turn.Attachments,
                IsExpanded = IsExpandedByDefault(category),
            });
        }

        foreach (var question in openQuestions)
        {
            var at = messages.FindIndex(m => m.Timestamp > question.Timestamp);
            messages.Insert(at < 0 ? messages.Count : at, question);
        }

        // The logs those entries pointed at were just discarded, so anything left would be a
        // dangling reference to a message that no longer exists. A reload mid-turn would
        // otherwise carry the previous view's spinner state into the restored transcript.
        return state with
        {
            Messages = messages.ToImmutable(),
            OpenActivityLogs = state.OpenActivityLogs.Clear(),
            ThinkingMessage = null,
        };
    }

    /// <summary>
    /// The display category for a restored history turn, from its AgentName and role.
    /// </summary>
    private static (MessageCategory Category, bool IsFromUser) CategorizeHistoryTurn(
        ConversationHistoryTurn turn, string? primaryAgentName)
    {
        if (turn.Role == "user")
        {
            // Subagent and worker synthetic turns are stored as "user" role with a subagent- or
            // worker- prefixed AgentName. Both represent spawned activity.
            if (turn.AgentName?.StartsWith("subagent-", StringComparison.OrdinalIgnoreCase) == true
                || turn.AgentName?.StartsWith("worker-", StringComparison.OrdinalIgnoreCase) == true)
                return (MessageCategory.SubagentActivity, false);

            // A2A synthetic turns have a non-null AgentName that differs from the primary agent.
            if (!string.IsNullOrEmpty(turn.AgentName) &&
                !string.Equals(turn.AgentName, primaryAgentName, StringComparison.OrdinalIgnoreCase))
                return (MessageCategory.A2AActivity, false);

            return (MessageCategory.UserInput, true);
        }

        return (MessageCategory.PrimaryFinal, false);
    }

    // ── Turns ───────────────────────────────────────────────────────────────

    private static ChatViewState OnUserMessageSent(ChatViewState state, UserMessageSent e) =>
        state with
        {
            Messages = state.Messages.Add(new ChatMessage
            {
                MessageId = e.MessageId,
                Content = e.Content,
                IsFromUser = true,
                Timestamp = e.Timestamp,
                UserId = e.UserId,
                SessionId = e.SessionId,
                Category = MessageCategory.UserInput,
                Attachments = e.Attachments,
            }),
        };

    // A new turn starts with a clean slate: no thinking text, and no activity log left open by
    // the last one.
    private static ChatViewState OnTurnStarted(ChatViewState state, TurnStarted e) =>
        state with
        {
            CurrentTurn = new TurnState(e.TurnId, e.Timestamp),
            ThinkingMessage = null,
            OpenActivityLogs = state.OpenActivityLogs.Clear(),
        };

    private static ChatViewState OnTurnEnded(ChatViewState state, TurnEnded e)
    {
        state = e.Outcome switch
        {
            TurnOutcome.Completed when e.Reply is not null =>
                AddReply(state with { ThinkingMessage = null }, e.Reply, MessageCategory.PrimaryFinal,
                    e.MessageId, e.Timestamp),
            TurnOutcome.TimedOut =>
                AddError(state with { ThinkingMessage = null }, "No reply received (timeout)",
                    e.MessageId, e.Timestamp),
            TurnOutcome.Failed =>
                AddError(state, $"Error: {e.Error}", e.MessageId, e.Timestamp),
            // Cancelled: the user already stopped waiting, and the agent posts its own
            // "Cancelled." reply.
            _ => state,
        };

        // Only the turn's own end releases the busy state. After a cancel or a newer turn, the
        // current turn (if any) is someone else's.
        return state.CurrentTurn?.TurnId == e.TurnId
            ? state with { CurrentTurn = null }
            : state;
    }

    // ── Agent output ────────────────────────────────────────────────────────

    private static ChatViewState AddReply(
        ChatViewState state, AgentReply reply, MessageCategory category, string messageId, DateTime at)
    {
        var formWaits = state.FormWaits;
        var logs = state.OpenActivityLogs;
        var turn = state.CurrentTurn;

        // The follow-up to a form answer ends that answer's wait (and only that one).
        if (reply.FollowsQuestionId is { } answered)
            formWaits = formWaits.Remove(answered);

        // Close the activity log for the source that just finished. PrimaryFinal closes the
        // PrimaryProgress log (different category names); subagent / A2A / scheduled final
        // replies close their own category's log.
        if (category == MessageCategory.PrimaryFinal)
        {
            logs = logs.Remove(ActivityLogKey(MessageCategory.PrimaryProgress, null));

            // The primary agent's turn is over, whoever was waiting on it. The view that started
            // the turn ends it too, but if that view is gone (reload, SignalR reconnect) its end
            // never comes, and the busy state would stick: a spinner under the reply and a
            // disabled input box.
            turn = null;
        }
        else
            logs = logs.Remove(ActivityLogKey(category, reply.AgentName));

        return state with
        {
            FormWaits = formWaits,
            OpenActivityLogs = logs,
            CurrentTurn = turn,
            Messages = state.Messages.Add(new ChatMessage
            {
                MessageId = messageId,
                Content = reply.Content,
                IsFromUser = false,
                Timestamp = at,
                AgentName = reply.AgentName,
                SessionId = reply.SessionId,
                ContentType = reply.ContentType,
                Attachments = reply.Attachments,
                IsInterim = !reply.IsFinal,
                Category = category,
                IsExpanded = IsExpandedByDefault(category),
            }),
        };
    }

    private static ChatViewState AddError(ChatViewState state, string message, string messageId, DateTime at) =>
        state with
        {
            Messages = state.Messages.Add(new ChatMessage
            {
                MessageId = messageId,
                Content = message,
                IsFromUser = false,
                IsError = true,
                Timestamp = at,
                Category = MessageCategory.Error,
                IsExpanded = true,
            }),
        };

    /// <summary>
    /// Adds an entry to the activity log for a source, starting the log if it isn't open.
    /// Concurrent sources (primary, subagent-X, scheduled-system) each get their own log.
    /// </summary>
    private static ChatViewState AppendActivity(
        ChatViewState state, MessageCategory category, string? agentName, string content,
        bool close, string messageId, DateTime at)
    {
        var key = ActivityLogKey(category, agentName);
        var messages = state.Messages;
        var logs = state.OpenActivityLogs;

        var index = logs.TryGetValue(key, out var logId)
            ? messages.FindIndex(m => m.MessageId == logId)
            : -1;

        ChatMessage log;
        if (index < 0)
        {
            log = new ChatMessage
            {
                MessageId = messageId,
                Content = content,
                IsFromUser = false,
                Timestamp = at,
                AgentName = agentName,
                Category = category,
                IsActivityLog = true,
                IsExpanded = false,
            };
            messages = messages.Add(log);
            index = messages.Count - 1;
            logs = logs.SetItem(key, log.MessageId);
        }
        else
            log = messages[index];

        // The summary line is the latest entry.
        messages = messages.SetItem(index, log with
        {
            Content = content,
            ActivityLogEntries = log.ActivityLogEntries.Add(new ActivityLogEntry(content, at)),
        });

        if (close)
            logs = logs.Remove(key);

        return state with { Messages = messages, OpenActivityLogs = logs };
    }

    /// <summary>
    /// Brings the view in line with the agent's own account of what's running: drops logs for
    /// subagents that have finished, starts logs for running subagents the view doesn't know
    /// about (after a reload, say), and adopts the agent's busy flag, so a reload lands on the
    /// real state rather than on whatever the previous view left behind.
    /// </summary>
    private static ChatViewState OnActiveStatusReconciled(ChatViewState state, ActiveStatusReconciled e)
    {
        var status = e.Status;
        var turn = status.IsProcessing
            ? state.CurrentTurn ?? new TurnState(ChatViewState.ReconciledTurnId, e.Timestamp)
            : null;

        var activeAgentNames = status.Subagents
            .Select(s => $"subagent-{s.TaskId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var subagentPrefix = $"{MessageCategory.SubagentActivity}:";
        var logs = state.OpenActivityLogs.RemoveRange(state.OpenActivityLogs.Keys
            .Where(k => k.StartsWith(subagentPrefix, StringComparison.Ordinal) &&
                        !activeAgentNames.Contains(k[subagentPrefix.Length..]))
            .ToList());

        var messages = state.Messages;
        foreach (var sub in status.Subagents)
        {
            var agentName = $"subagent-{sub.TaskId}";
            var key = ActivityLogKey(MessageCategory.SubagentActivity, agentName);
            if (logs.ContainsKey(key))
                continue;

            var content = $"Running: {sub.Description}";
            var log = new ChatMessage
            {
                MessageId = $"{e.IdPrefix}-{sub.TaskId}",
                Content = content,
                IsFromUser = false,
                Timestamp = sub.StartedAt.UtcDateTime,
                AgentName = agentName,
                Category = MessageCategory.SubagentActivity,
                IsActivityLog = true,
                IsExpanded = false,
                ActivityLogEntries = [new ActivityLogEntry(content, sub.StartedAt.UtcDateTime)],
            };
            messages = messages.Add(log);
            logs = logs.SetItem(key, log.MessageId);
        }

        return state with { CurrentTurn = turn, OpenActivityLogs = logs, Messages = messages };
    }

    // ── MCP questions ───────────────────────────────────────────────────────

    /// <summary>
    /// Adds a handed-back MCP question as a form. It arrives in the middle of the agent's turn
    /// (the agent is still writing its reply), so unlike a final reply it leaves the busy state
    /// and the activity log alone. A question already shown is ignored.
    /// </summary>
    private static ChatReduction OnMcpQuestionReceived(ChatViewState state, McpQuestionReceived e)
    {
        if (e.Form is not null && state.FindQuestion(e.Form.QuestionId) is not null)
            return Reject(state);

        return Accept(state with
        {
            Messages = state.Messages.Add(new ChatMessage
            {
                MessageId = e.MessageId,
                Content = e.Reply.Content,
                IsFromUser = false,
                Timestamp = e.Timestamp,
                AgentName = e.Reply.AgentName,
                SessionId = e.Reply.SessionId,
                ContentType = e.Reply.ContentType,
                Category = MessageCategory.McpQuestion,
                Question = e.Form,
                IsExpanded = true,
            }),
        });
    }

    /// <summary>
    /// Applies a state change to a question's form. A closed form stays closed: a late
    /// <c>rejected</c> can't reopen it. Unknown ids are ignored.
    /// </summary>
    private static ChatReduction OnMcpQuestionStatusChanged(ChatViewState state, McpQuestionStatus status)
    {
        var msg = state.FindQuestion(status.QuestionId);
        if (msg is null || McpQuestionState.IsClosed(msg.QuestionState))
            return Reject(state);

        // Only an accepted answer or decline leads to an agent reply; anything else ends this
        // form's wait here. A turn that's still running stays busy on its own account.
        var formWaits = status.State is McpQuestionState.Answered or McpQuestionState.Declined
            ? state.FormWaits
            : state.FormWaits.Remove(status.QuestionId);

        return Accept(state with
        {
            FormWaits = formWaits,
            Messages = state.Messages.Replace(msg, msg with
            {
                QuestionState = status.State,
                QuestionReason = status.Reason,
                QuestionAnsweredBy = status.AnsweredBy,
                IsSubmitting = false,
            }),
        });
    }

    /// <summary>
    /// Marks a form as sent and the chat busy until the agent's follow-up arrives. Refused when
    /// the form is unknown, closed, expired, or already being sent (another tab got there first).
    /// </summary>
    private static ChatReduction OnMcpAnswerSubmitted(ChatViewState state, McpAnswerSubmitted e)
    {
        var msg = state.FindQuestion(e.QuestionId);
        if (msg is null || msg.IsSubmitting || !msg.IsQuestionOpen(AsOffset(e.Timestamp)))
            return Reject(state);

        return Accept(state with
        {
            FormWaits = state.FormWaits.SetItem(e.QuestionId, e.Timestamp),
            Messages = state.Messages.Replace(msg, msg with
            {
                IsSubmitting = true,
                QuestionState = null,
                QuestionReason = null,
            }),
        });
    }

    // ── User actions ────────────────────────────────────────────────────────

    /// <summary>Records a thumbs-up or thumbs-down. A message can be rated once.</summary>
    private static ChatReduction OnFeedbackRecorded(ChatViewState state, FeedbackRecorded e)
    {
        var msg = state.FindMessage(e.MessageId);
        if (msg is null || msg.Feedback != FeedbackState.None)
            return Reject(state);

        return Accept(state with
        {
            Messages = state.Messages.Replace(msg, msg with
            {
                Feedback = e.IsPositive ? FeedbackState.ThumbsUp : FeedbackState.ThumbsDown,
            }),
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ChatReduction UpdateMessage(
        ChatViewState state, string messageId, Func<ChatMessage, ChatMessage> update)
    {
        var msg = state.FindMessage(messageId);
        return msg is null
            ? Reject(state)
            : Accept(state with { Messages = state.Messages.Replace(msg, update(msg)) });
    }

    private static bool IsExpandedByDefault(MessageCategory category) =>
        category is MessageCategory.PrimaryFinal
            or MessageCategory.ScheduledUser
            or MessageCategory.UserInput
            or MessageCategory.Error;

    /// <summary>
    /// The key an activity log is tracked by. The primary agent has one progress log however its
    /// replies are named; other sources get one per category and agent name.
    /// </summary>
    private static string ActivityLogKey(MessageCategory category, string? agentName) =>
        category == MessageCategory.PrimaryProgress
            ? $"{category}:"
            : $"{category}:{agentName ?? ""}";

    private static DateTimeOffset AsOffset(DateTime utc) =>
        new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static ChatReduction Accept(ChatViewState state) => new(state, true);
    private static ChatReduction Reject(ChatViewState state) => new(state, false);
}
