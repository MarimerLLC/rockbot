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
        ActivityReported x => OnActivityReported(state, x),
        ReplyReceived x => Accept(AddReply(state with { ThinkingMessage = null }, x.Reply, x.Category,
            x.MessageId, x.Timestamp)),
        ErrorReported x => Accept(AddError(state, x.Message, x.MessageId, x.Timestamp)),
        ActiveStatusReconciled x => Accept(OnActiveStatusReconciled(state, x)),
        TurnHeartbeat x => OnTurnHeartbeat(state, x),
        LateRunFinished x => state.CurrentTurn is null && state.ThinkingMessage is not null
            ? Accept(state with { ThinkingMessage = null })
            : Reject(state),
        ToolCallReported x => OnToolCallReported(state, x),
        SubagentRunStarted x => Accept(OnSubagentRunStarted(state, x)),
        SubagentRunFinished x => Accept(OnSubagentRunFinished(state, x)),
        TaskListSnapshotReceived x => OnTaskListSnapshot(state, x),
        TaskListDeltaReceived x => OnTaskListDelta(state, x),
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

    // A new turn starts with a clean slate for the primary agent: no thinking text, no progress
    // log left open by the last turn, and no plan from it. Subagent, worker and background logs
    // stay open: that work is still running, and its own lifecycle events close them.
    private static ChatViewState OnTurnStarted(ChatViewState state, TurnStarted e) =>
        state with
        {
            CurrentTurn = new TurnState(e.TurnId, e.Timestamp),
            ThinkingMessage = null,
            OpenActivityLogs = state.OpenActivityLogs.Remove(ActivityLogKey(MessageCategory.PrimaryProgress, null)),
            TaskLists = state.TaskLists.Remove(ReplySourceKind.Primary),
        };

    private static ChatReduction OnTurnHeartbeat(ChatViewState state, TurnHeartbeat e)
    {
        // A heartbeat for the running turn, or (after a timeout) for a run this view gave up on
        // while nothing else is running: either way the agent is still at work.
        var forCurrent = state.CurrentTurn is { } turn
            && (turn.TurnId == e.TurnId || turn.TurnId == ChatViewState.ReconciledTurnId);
        if (!forCurrent && state.CurrentTurn is not null)
            return Reject(state);

        var thinking = string.IsNullOrWhiteSpace(e.Status)
            ? state.ThinkingMessage ?? (forCurrent ? null : "Still working…")
            : e.Status;
        return thinking == state.ThinkingMessage ? Accept(state) : Accept(state with { ThinkingMessage = thinking });
    }

    private static ChatViewState OnTurnEnded(ChatViewState state, TurnEnded e)
    {
        // A late reply from a turn the user already moved on from (cancelled, then sent again)
        // is still shown, but the newer turn, its progress log and its thinking text are left
        // alone. A turn adopted from the agent's active status is not "newer": it is most likely
        // this very turn, seen from a reloaded view, and its reply has to end it (#538).
        var newerTurnRunning = state.CurrentTurn is { } current
            && current.TurnId != e.TurnId
            && current.TurnId != ChatViewState.ReconciledTurnId;

        state = e.Outcome switch
        {
            TurnOutcome.Completed when e.Reply is not null && newerTurnRunning =>
                AddReply(state, e.Reply, MessageCategory.PrimaryFinal, e.MessageId, e.Timestamp,
                    closesSource: false),
            TurnOutcome.Completed when e.Reply is not null =>
                AddReply(state with { ThinkingMessage = null }, e.Reply, MessageCategory.PrimaryFinal,
                    e.MessageId, e.Timestamp),
            TurnOutcome.TimedOut =>
                AddError(state with { ThinkingMessage = null }, "No reply received (timeout)",
                    e.MessageId, e.Timestamp),
            TurnOutcome.Failed =>
                AddError(state, $"Error: {e.Error}", e.MessageId, e.Timestamp),
            // The agent reported the run failed: show its error reply if it sent one (that
            // reply ends the turn like any final reply), else its error text.
            TurnOutcome.AgentError when e.Reply is not null =>
                AddReply(state with { ThinkingMessage = newerTurnRunning ? state.ThinkingMessage : null },
                    e.Reply, MessageCategory.PrimaryFinal, e.MessageId, e.Timestamp, closesSource: !newerTurnRunning),
            TurnOutcome.AgentError =>
                CloseThisTurnsProgress(
                    AddError(state, $"Error: {e.Error ?? "the agent reported an error"}", e.MessageId, e.Timestamp),
                    newerTurnRunning),
            // The agent went silent mid-run. Unlike a timeout there is nothing left to wait for,
            // so the progress log's spinner stops too.
            TurnOutcome.AgentLost =>
                CloseThisTurnsProgress(
                    AddError(state, "Lost contact with the agent" + (e.Error is null ? "" : $": {e.Error}"),
                        e.MessageId, e.Timestamp),
                    newerTurnRunning),
            // Cancelled by the agent (another client, or the agent itself) while this view was
            // still waiting: say so. A reply the agent sent with it is shown instead.
            TurnOutcome.Cancelled when state.CurrentTurn?.TurnId == e.TurnId && e.Reply is not null =>
                AddReply(state with { ThinkingMessage = null }, e.Reply, MessageCategory.PrimaryFinal,
                    e.MessageId, e.Timestamp),
            TurnOutcome.Cancelled when state.CurrentTurn?.TurnId == e.TurnId =>
                CloseThisTurnsProgress(
                    AddError(state, "Turn cancelled", e.MessageId, e.Timestamp),
                    newerTurnRunning: false),
            // Cancelled by the user: they already stopped waiting, and the agent posts its own
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

    /// <param name="closesSource">
    /// False for a reply whose source has already moved on (a stale turn's reply): it is shown,
    /// but the source's current activity log and turn belong to newer work and stay open.
    /// </param>
    private static ChatViewState AddReply(
        ChatViewState state, AgentReply reply, MessageCategory category, string messageId, DateTime at,
        bool closesSource = true)
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
        if (closesSource && category == MessageCategory.PrimaryFinal)
        {
            logs = logs.Remove(ActivityLogKey(MessageCategory.PrimaryProgress, null));

            // The primary agent's turn is over, whoever was waiting on it. The view that started
            // the turn ends it too, but if that view is gone (reload, SignalR reconnect) its end
            // never comes, and the busy state would stick: a spinner under the reply and a
            // disabled input box.
            turn = null;
        }
        else if (closesSource)
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

        // Newer agents list subagents and workers in Work; older ones only subagents.
        var running = status.Work is { } work
            ? work.Select(w => (w.TaskId, w.Kind, w.Description, w.StartedAt)).ToList()
            : status.Subagents.Select(s => (s.TaskId, Kind: ReplySourceKind.Subagent, s.Description, s.StartedAt)).ToList();

        var activeAgentNames = running
            .Select(w => SubagentAgentName(w.Kind, w.TaskId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var subagentPrefix = $"{MessageCategory.SubagentActivity}:";
        var logs = state.OpenActivityLogs.RemoveRange(state.OpenActivityLogs.Keys
            .Where(k => k.StartsWith(subagentPrefix, StringComparison.Ordinal) &&
                        !activeAgentNames.Contains(k[subagentPrefix.Length..]))
            .ToList());

        var messages = state.Messages;
        foreach (var sub in running)
        {
            var agentName = SubagentAgentName(sub.Kind, sub.TaskId);
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

        // Plans of subagents that are no longer running go with them. Only an agent that lists
        // its workers too can say which ones those are.
        var taskLists = state.TaskLists;
        if (status.Work is not null)
        {
            var runningIds = running.Select(w => w.TaskId).ToHashSet(StringComparer.Ordinal);
            taskLists = taskLists.RemoveRange(taskLists.Keys
                .Where(k => k != ReplySourceKind.Primary && !runningIds.Contains(k))
                .ToList());
        }

        return state with { CurrentTurn = turn, OpenActivityLogs = logs, Messages = messages, TaskLists = taskLists };
    }

    /// <summary>
    /// The agent name a subagent's or worker's activity log is tracked under:
    /// <c>subagent-{taskId}</c> or <c>worker-{taskId}</c>, as their own replies are named.
    /// </summary>
    public static string SubagentAgentName(string? kind, string taskId) =>
        string.Equals(kind, ReplySourceKind.Worker, StringComparison.OrdinalIgnoreCase)
            ? $"worker-{taskId}"
            : $"subagent-{taskId}";

    // ── Structured agent events (#795) ──────────────────────────────────────

    /// <summary>
    /// Adds a tool call to its source's activity log, or updates the entry for the same call id
    /// in place (started, then finished or failed). A report for a subagent or worker that has
    /// already finished updates its closed log rather than opening a new one: lifecycle events and
    /// progress arrive on different topics, so a tool's last report can trail the run's end.
    /// </summary>
    private static ChatReduction OnToolCallReported(ChatViewState state, ToolCallReported e)
    {
        var key = ActivityLogKey(e.Category, e.AgentName);
        var finishedRun = !state.OpenActivityLogs.ContainsKey(key) && IsFinishedRun(state, e.Category, e.AgentName);

        var index = state.OpenActivityLogs.TryGetValue(key, out var logId)
            ? state.Messages.FindIndex(m => m.MessageId == logId)
            : finishedRun
                ? state.Messages.FindLastIndex(m => m.Category == e.Category && m.AgentName == e.AgentName
                    && m.ActivityLogEntries.Any(x => x.ToolCall?.ToolCallId == e.ToolCall.ToolCallId))
                : -1;

        if (index >= 0)
        {
            var log = state.Messages[index];
            var entryIndex = log.ActivityLogEntries.FindIndex(x => x.ToolCall?.ToolCallId == e.ToolCall.ToolCallId);
            if (entryIndex >= 0)
            {
                var entry = log.ActivityLogEntries[entryIndex];
                // Only a call's start carries its arguments; keep them on the later reports.
                var call = e.ToolCall.ArgsSummary is null && entry.ToolCall?.ArgsSummary is { } args
                    ? e.ToolCall with { ArgsSummary = args }
                    : e.ToolCall;
                return Accept(state with
                {
                    Messages = state.Messages.SetItem(index, log with
                    {
                        Content = finishedRun ? log.Content : e.Content,
                        ActivityLogEntries = log.ActivityLogEntries.SetItem(entryIndex,
                            entry with { Content = e.Content, ToolCall = call }),
                    }),
                });
            }
        }

        // A new call from a run that has finished: nothing left to show it in.
        if (finishedRun)
            return Reject(state);

        state = AppendActivity(state, e.Category, e.AgentName, e.Content, close: false, e.MessageId, e.Timestamp);

        // The entry just added is the log's last one; tag it with the call.
        var openId = state.OpenActivityLogs[key];
        var at = state.Messages.FindIndex(m => m.MessageId == openId);
        var updated = state.Messages[at];
        var last = updated.ActivityLogEntries.Count - 1;
        return Accept(state with
        {
            Messages = state.Messages.SetItem(at, updated with
            {
                ActivityLogEntries = updated.ActivityLogEntries.SetItem(last,
                    updated.ActivityLogEntries[last] with { ToolCall = e.ToolCall }),
            }),
        });
    }

    /// <summary>
    /// Progress for a subagent or worker that has already finished and whose log is closed. It
    /// would open a log nothing closes again (a spinner forever), so it is dropped; a completion
    /// report still goes through, since it closes what it opens.
    /// </summary>
    private static ChatReduction OnActivityReported(ChatViewState state, ActivityReported e) =>
        !e.IsCompletion
        && !state.OpenActivityLogs.ContainsKey(ActivityLogKey(e.Category, e.AgentName))
        && IsFinishedRun(state, e.Category, e.AgentName)
            ? Reject(state)
            : Accept(AppendActivity(state, e.Category, e.AgentName, e.Content, e.IsCompletion, e.MessageId, e.Timestamp));

    private static bool IsFinishedRun(ChatViewState state, MessageCategory category, string? agentName) =>
        category == MessageCategory.SubagentActivity && agentName is not null && state.FinishedRuns.Contains(agentName);

    private static ChatViewState OnSubagentRunStarted(ChatViewState state, SubagentRunStarted e)
    {
        var s = e.Started;
        var agentName = SubagentAgentName(s.Kind, s.SubagentRunId);
        if (state.OpenActivityLogs.ContainsKey(ActivityLogKey(MessageCategory.SubagentActivity, agentName)))
            return state;

        var label = s.Kind == ReplySourceKind.Worker ? "Worker" : "Subagent";
        return AppendActivity(state, MessageCategory.SubagentActivity, agentName,
            $"{label} started: {s.Description}", close: false, e.MessageId, e.Timestamp);
    }

    /// <summary>How many finished subagent and worker runs <see cref="ChatViewState.FinishedRuns"/> remembers.</summary>
    internal const int MaxFinishedRuns = 256;

    private static ChatViewState OnSubagentRunFinished(ChatViewState state, SubagentRunFinished e)
    {
        var f = e.Finished;
        var agentName = SubagentAgentName(f.Kind, f.SubagentRunId);
        var key = ActivityLogKey(MessageCategory.SubagentActivity, agentName);
        var finishedRuns = (state.FinishedRuns.Count >= MaxFinishedRuns ? state.FinishedRuns.Clear() : state.FinishedRuns)
            .Add(agentName);
        state = state with { TaskLists = state.TaskLists.Remove(f.SubagentRunId), FinishedRuns = finishedRuns };

        // Its log already closed (its result arrived first): nothing more to show.
        if (!state.OpenActivityLogs.ContainsKey(key))
            return state;

        var content = f.Outcome switch
        {
            SubagentOutcome.Success => "Finished",
            SubagentOutcome.Cancelled => "Cancelled",
            SubagentOutcome.Timeout => "Timed out",
            _ => string.IsNullOrWhiteSpace(f.Error) ? "Failed" : $"Failed: {f.Error}",
        };
        return AppendActivity(state, MessageCategory.SubagentActivity, agentName,
            content, close: true, e.MessageId, e.Timestamp);
    }

    /// <summary>
    /// Whether a task list may be shown, and under which key. A subagent's or worker's list is
    /// keyed by its task id. Anything else is shown as the user's plan, so it must belong to a
    /// turn of this view: the running one, the reconciled one, or (with nothing running) the
    /// last one. A list with no run id, or from an older turn, is turned away, so a late event
    /// can't replace or check off the plan of the turn that followed.
    /// </summary>
    private static string? TaskListKey(ChatViewState state, ReplySource source, string? runId)
    {
        var key = TaskListStatus.KeyFor(source);
        if (key != ReplySourceKind.Primary)
            return key;

        if (runId is null)
            return null;

        return state.CurrentTurn is not { } turn
               || turn.TurnId == runId
               || turn.TurnId == ChatViewState.ReconciledTurnId
            ? key
            : null;
    }

    private static ChatReduction OnTaskListSnapshot(ChatViewState state, TaskListSnapshotReceived e)
    {
        var s = e.Snapshot;
        if (TaskListKey(state, s.Source, s.RunId) is not { } key)
            return Reject(state);

        return Accept(state with
        {
            TaskLists = state.TaskLists.SetItem(key,
                new TaskListView(key, s.Source, s.RunId, [.. s.Items], e.Timestamp)),
        });
    }

    private static ChatReduction OnTaskListDelta(ChatViewState state, TaskListDeltaReceived e)
    {
        var d = e.Delta;
        if (TaskListKey(state, d.Source, d.RunId) is not { } key)
            return Reject(state);

        // A delta for another run than the list on show starts that run's list afresh.
        var list = state.TaskLists.TryGetValue(key, out var existing) && existing.RunId == d.RunId
            ? existing
            : new TaskListView(key, d.Source, d.RunId, [], e.Timestamp);

        var index = list.Items.FindIndex(i => i.Id == d.Item.Id);
        var items = index >= 0 ? list.Items.SetItem(index, d.Item) : list.Items.Add(d.Item);
        return Accept(state with
        {
            TaskLists = state.TaskLists.SetItem(key, list with { Items = items, UpdatedAt = e.Timestamp }),
        });
    }

    /// <summary>Stops the primary progress log's spinner and clears the thinking text, unless a newer turn owns them.</summary>
    private static ChatViewState CloseThisTurnsProgress(ChatViewState state, bool newerTurnRunning) =>
        newerTurnRunning
            ? state
            : state with
            {
                ThinkingMessage = null,
                OpenActivityLogs = state.OpenActivityLogs.Remove(ActivityLogKey(MessageCategory.PrimaryProgress, null)),
            };

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
