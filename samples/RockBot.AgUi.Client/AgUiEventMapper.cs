using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AGUI.Abstractions;
using RockBot.UserProxy;
using RockBot.UserProxy.ChatState;

namespace RockBot.AgUi.Client;

/// <summary>
/// Turns the AG-UI events of one stream into <see cref="ChatEvent"/>s for
/// <see cref="ChatStateService"/>: the client-side counterpart of the Blazor Server UI's
/// <c>BlazorUserFrontend</c> and <c>ChatTurnObserver</c>, which do the same from the message bus's
/// callbacks. One instance reads one run (<see cref="ForRun"/>) or one thread's notify stream
/// (<see cref="ForNotify"/>).
/// </summary>
/// <remarks>
/// <para>
/// No I/O and no browser dependency: events in, chat events out. It does keep what a stream
/// spreads over several events (a message's text, the tool call a result belongs to, which
/// subagent is a worker), so it is not thread-safe; one reader calls it in order.
/// </para>
/// <para>
/// The chat state was written for the bus, where a reply is one object. On the wire it is three
/// to five events (<c>TEXT_MESSAGE_START</c>, <c>CONTENT</c>, <c>END</c>, then
/// <c>rockbot.attachments</c> and <c>rockbot.origin</c>), so the mapper holds a message until the
/// event that completes it and then emits the one chat event the reducer expects.
/// </para>
/// </remarks>
public sealed class AgUiEventMapper
{
    private readonly string _threadId;
    private readonly string? _runId;
    private readonly TimeProvider _clock;
    private readonly string _channel;

    private readonly Dictionary<string, ToolCall> _toolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _subagentKinds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openActivities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _attachmentUrls = new(StringComparer.Ordinal);
    private PendingMessage? _pending;
    private bool _ended;

    private AgUiEventMapper(string threadId, string? runId, TimeProvider? clock, string channel)
    {
        _threadId = threadId;
        _runId = runId;
        _clock = clock ?? TimeProvider.System;
        _channel = channel;
    }

    /// <summary>A mapper for the stream of one run (<c>POST /agui</c>). The run's id is the turn's id.</summary>
    public static AgUiEventMapper ForRun(string threadId, string runId, TimeProvider? clock = null, string channel = "agui") =>
        new(threadId, runId, clock, channel);

    /// <summary>A mapper for a thread's notify stream (<c>GET /agui/notify</c>).</summary>
    public static AgUiEventMapper ForNotify(string threadId, TimeProvider? clock = null, string channel = "agui") =>
        new(threadId, null, clock, channel);

    private bool IsRun => _runId is not null;

    /// <summary>
    /// The primary agent's display name, when the client knows it. Tells the agent's own progress
    /// on the notify stream from a remote (A2A) agent's.
    /// </summary>
    public string? PrimaryAgentName { get; set; }

    /// <summary>The agent's version, carried along when its name changes.</summary>
    public string AgentVersion { get; set; } = "";

    /// <summary>
    /// The run the client stopped waiting for (it ended with <c>rockbot.reply_pending</c>). The
    /// notify stream reports that run's task list under the key <c>primary</c> without saying
    /// which run it is; the chat state only shows a primary list that names its run.
    /// </summary>
    public string? LateRunId { get; set; }

    /// <summary>True once a run's stream said its reply is still to come (<c>rockbot.reply_pending</c>).</summary>
    public bool ReplyPending { get; private set; }

    /// <summary>True once a run's terminal event (<c>RUN_FINISHED</c> or <c>RUN_ERROR</c>) was mapped.</summary>
    public bool Ended => _ended;

    /// <summary>
    /// The <c>url</c> each attachment arrived with, by its <c>path</c>. The chat state keeps an
    /// attachment as the bus has it (no URL), so a view looks the URL up here.
    /// </summary>
    public IReadOnlyDictionary<string, string> AttachmentUrls => _attachmentUrls;

    /// <summary>The two events that start a turn in the chat state, before the request is sent.</summary>
    public static IReadOnlyList<ChatEvent> BeginTurn(
        string threadId, string runId, string text, string userId, IReadOnlyList<AgentAttachment>? attachments = null) =>
    [
        new UserMessageSent(text, userId, threadId, attachments is { Count: > 0 } ? attachments : null),
        new TurnStarted(runId),
    ];

    /// <summary>
    /// Forgets everything held from earlier events. For a notify stream that was told to resync:
    /// what came before can't be continued.
    /// </summary>
    public void Reset()
    {
        _pending = null;
        _toolCalls.Clear();
        _openActivities.Clear();
    }

    /// <summary>The chat events for one AG-UI event; empty when it changes nothing the chat shows.</summary>
    public IReadOnlyList<ChatEvent> Map(BaseEvent e)
    {
        var events = new List<ChatEvent>();
        if (_ended)
            return events;

        // On the notify stream a message is complete at rockbot.origin. Should that event be
        // missing, the next unrelated event completes it instead, so no message is held for ever.
        if (!IsRun && _pending is { Ended: true } && !ContinuesMessage(e))
            events.Add(Unsolicited(TakePending()!, origin: null, e));

        switch (e)
        {
            case TextMessageStartEvent x:
                if (TakePending() is { } earlier)
                    events.Add(Unsolicited(earlier, origin: null, e));
                _pending = new PendingMessage(x.MessageId, x.Name, x.SubagentRunId, x.Timestamp);
                break;

            case TextMessageContentEvent x when _pending?.Id == x.MessageId:
                _pending.Text.Append(x.Delta);
                break;

            case TextMessageEndEvent x when _pending?.Id == x.MessageId:
                _pending.Ended = true;
                break;

            case ActivitySnapshotEvent x when x.ActivityType == RockBotEvents.ProgressActivityType:
                MapActivity(x, events);
                break;

            case ToolCallStartEvent x:
                MapToolCallStart(x, events);
                break;

            case ToolCallArgsEvent x:
                MapToolCallArgs(x, events);
                break;

            case ToolCallResultEvent x:
                MapToolCallResult(x, events);
                break;

            case StateSnapshotEvent x:
                MapStateSnapshot(x, events);
                break;

            case StateDeltaEvent x:
                MapStateDelta(x, events);
                break;

            case SubagentStartedEvent x:
                MapSubagentStarted(x, events);
                break;

            case SubagentFinishedEvent { SubagentRunId: { } finishedId } x:
                MapSubagentFinished(x, finishedId, SubagentOutcome.Success, error: null, events);
                break;

            case SubagentErrorEvent { SubagentRunId: { } failedId } x:
                MapSubagentFinished(x, failedId, x.Code ?? SubagentOutcome.Failed, x.Message, events);
                break;

            case CustomEvent x:
                MapCustom(x, events);
                break;

            case RunFinishedEvent x when IsRun:
                events.Add(Stamp(EndTurn(x), e));
                _ended = true;
                break;

            case RunErrorEvent x when IsRun:
                events.Add(Stamp(FailTurn(x), e));
                _ended = true;
                break;

            // RUN_STARTED: the client started the turn when it sent the message (BeginTurn).
            // TOOL_CALL_END, TEXT_MESSAGE_* for another message, anything newer: nothing to show.
        }

        return events;
    }

    /// <summary>
    /// A run's stream ended. When it ended without <c>RUN_FINISHED</c> or <c>RUN_ERROR</c> (the
    /// connection dropped), the turn is ended as failed, so the chat doesn't stay busy.
    /// </summary>
    public IReadOnlyList<ChatEvent> StreamEnded(string? reason = null)
    {
        if (!IsRun || _ended)
            return [];

        _ended = true;
        return [new TurnEnded(_runId!, TurnOutcome.Failed) { Error = reason ?? "the connection to the proxy was lost before the run ended" }];
    }

    // ── Replies ──────────────────────────────────────────────────────────────

    private static bool ContinuesMessage(BaseEvent e) =>
        e is CustomEvent { Name: RockBotEvents.Attachments or RockBotEvents.Origin };

    private PendingMessage? TakePending()
    {
        var pending = _pending;
        _pending = null;
        return pending;
    }

    private TurnEnded EndTurn(RunFinishedEvent finished)
    {
        var reply = TakePending();
        var turnId = _runId!;

        if (finished.Outcome is RunFinishedCancelledOutcome)
            return WithReply(new TurnEnded(turnId, TurnOutcome.Cancelled), reply);

        // The proxy stopped waiting; the agent may still be at it, and the reply then arrives on
        // the notify stream. The chat state's name for that is a timed-out turn.
        if (reply is null && ReplyPending)
            return new TurnEnded(turnId, TurnOutcome.TimedOut);

        // An interrupt outcome can't happen in custom question mode. If it does, the questions
        // were already shown as rockbot.mcp_question events, so the turn just ends.
        return WithReply(new TurnEnded(turnId, TurnOutcome.Completed), reply);
    }

    private TurnEnded FailTurn(RunErrorEvent error)
    {
        var reply = TakePending();
        var turnId = _runId!;
        return error.Code switch
        {
            RockBotEvents.ErrorCodes.AgentLost => new TurnEnded(turnId, TurnOutcome.AgentLost) { Error = error.Message },
            RockBotEvents.ErrorCodes.Error => WithReply(new TurnEnded(turnId, TurnOutcome.AgentError) { Error = error.Message }, reply),
            // The proxy's own refusals and failures (interrupts_pending, client_error,
            // proxy_stopping, …): the turn failed for the reason given.
            _ => new TurnEnded(turnId, TurnOutcome.Failed) { Error = error.Message },
        };
    }

    private TurnEnded WithReply(TurnEnded ended, PendingMessage? reply) =>
        reply is null
            ? ended
            : ended with { Reply = ToReply(reply, _threadId, reply.Name, isFinal: true), MessageId = reply.Id };

    /// <summary>A reply nobody was waiting for, with where it came from.</summary>
    private ChatEvent Unsolicited(PendingMessage message, OriginValue? origin, BaseEvent at)
    {
        var source = origin?.Source;
        var sessionId = origin?.SessionId ?? _threadId;
        var subagent = SubagentName(source, message.SubagentRunId);
        var category = Categorize(source, sessionId, subagent is not null);

        var reply = ToReply(message, sessionId, subagent ?? message.Name, isFinal: subagent is null) with
        {
            Source = source,
            Origin = origin?.Origin,
            FollowsQuestionId = origin?.FollowsQuestionId,
        };

        // Where the work started, as a quoted line above the reply, as the Blazor UI shows it. A
        // reply to something asked on this thread through this channel needs none.
        reply = reply with
        {
            Content = ReplyOriginFormatter.WithQuotedAnchor(reply.Content, reply.Origin, _channel, sessionId, _clock.GetUtcNow()),
        };

        if (subagent is not null)
            _openActivities.Remove(subagent);

        return Stamp(new ReplyReceived(reply, category) { MessageId = message.Id }, at, message.Timestamp);
    }

    private AgentReply ToReply(PendingMessage message, string sessionId, string? agentName, bool isFinal) => new()
    {
        Content = message.Text.ToString(),
        SessionId = sessionId,
        AgentName = agentName ?? PrimaryAgentName ?? "",
        IsFinal = isFinal,
        Attachments = message.Attachments,
    };

    /// <summary>
    /// The display category of an unsolicited reply, from what the proxy says of its source. The
    /// same rules as the Blazor UI's <c>CategorizeReply</c>, which has the whole reply to go by.
    /// </summary>
    private static MessageCategory Categorize(ReplySource? source, string sessionId, bool fromSubagent)
    {
        if (fromSubagent)
            return MessageCategory.SubagentActivity;

        var inbound = sessionId.StartsWith(RockBotEvents.Sessions.A2AInbound, StringComparison.OrdinalIgnoreCase);
        return source?.Kind switch
        {
            ReplySourceKind.Scheduled => sessionId == RockBotEvents.Sessions.ScheduledSystem
                ? MessageCategory.ScheduledSystem
                : MessageCategory.ScheduledUser,
            ReplySourceKind.A2A when inbound => MessageCategory.A2AActivity,
            _ when sessionId == RockBotEvents.Sessions.ScheduledSystem => MessageCategory.ScheduledSystem,
            _ when inbound => MessageCategory.A2AActivity,
            _ => MessageCategory.PrimaryFinal,
        };
    }

    // ── Progress and tool calls ──────────────────────────────────────────────

    private void MapActivity(ActivitySnapshotEvent activity, List<ChatEvent> events)
    {
        var content = Read(activity.Content, WireJson.Default.ActivityContent);
        if (content is null)
            return;

        var (category, agentName) = Source(activity.SubagentRunId, content.AgentName);

        if (content.Done)
        {
            // The source finished. Close its log if it has one; its result follows as a message.
            if (agentName is not null && _openActivities.Remove(agentName))
                events.Add(Stamp(new ActivityReported(category, agentName, "Finished", IsCompletion: true), activity));
            return;
        }

        if (string.IsNullOrWhiteSpace(content.Text))
            return;

        if (category == MessageCategory.PrimaryProgress && IsRun)
        {
            events.Add(Stamp(new TurnProgress(_runId!, content.Text), activity));
            return;
        }

        if (agentName is not null)
            _openActivities.Add(agentName);
        events.Add(Stamp(new ActivityReported(category, agentName, content.Text), activity));
    }

    private void MapToolCallStart(ToolCallStartEvent start, List<ChatEvent> events)
    {
        var (category, agentName) = Source(start.SubagentRunId, agentName: null);
        var call = new ToolCall(start.ToolCallName, category, agentName);
        _toolCalls[start.ToolCallId] = call;
        if (agentName is not null)
            _openActivities.Add(agentName);

        events.Add(Stamp(Report(call, new ToolCallInfo
        {
            ToolCallId = start.ToolCallId, ToolCallName = call.Name, Phase = ToolCallPhase.Started,
        }), start));
    }

    private void MapToolCallArgs(ToolCallArgsEvent args, List<ChatEvent> events)
    {
        // RockBot sends a readable summary of the arguments, wrapped as {"summary": "…"}.
        if (!_toolCalls.TryGetValue(args.ToolCallId, out var call)
            || Read(args.Delta, WireJson.Default.ToolArgs)?.Summary is not { Length: > 0 } summary)
            return;

        // The same call again, now with its arguments: the chat state updates the entry in place.
        events.Add(Stamp(Report(call, new ToolCallInfo
        {
            ToolCallId = args.ToolCallId, ToolCallName = call.Name, Phase = ToolCallPhase.Started, ArgsSummary = summary,
        }), args));
    }

    private void MapToolCallResult(ToolCallResultEvent result, List<ChatEvent> events)
    {
        var detail = Read(result.Metadata, WireJson.Default.ToolResultMetadata)?.Rockbot;
        if (!_toolCalls.Remove(result.ToolCallId, out var call))
        {
            // A result whose start this stream never showed (it joined late): still worth a line.
            var (category, agentName) = Source(result.SubagentRunId, agentName: null);
            call = new ToolCall(result.ToolCallId, category, agentName);
        }

        var content = result.Content;
        var text = content.IsText ? content.Value as string ?? "" : content.ToString() ?? "";
        var failed = detail?.Failed == true;
        events.Add(Stamp(Report(call, new ToolCallInfo
        {
            ToolCallId = result.ToolCallId,
            ToolCallName = call.Name,
            Phase = failed ? ToolCallPhase.Failed : ToolCallPhase.Finished,
            Result = failed ? null : text,
            Error = failed ? text : null,
            DurationMs = detail?.DurationMs,
        }), result));
    }

    /// <summary>
    /// The chat event for a tool call. The bus sends a line of text with each call; the AG-UI
    /// events don't carry it, so it is written here.
    /// </summary>
    private static ToolCallReported Report(ToolCall call, ToolCallInfo info) =>
        new(call.Category, call.AgentName, info, info.Phase switch
        {
            ToolCallPhase.Failed => $"{call.Name} failed: {info.Error}",
            ToolCallPhase.Finished => info.DurationMs is { } ms ? $"{call.Name} finished ({ms} ms)" : $"{call.Name} finished",
            _ => info.ArgsSummary is { Length: > 0 } args ? $"Calling {call.Name} ({args})…" : $"Calling {call.Name}…",
        });

    /// <summary>
    /// Whose activity log an event belongs in. A subagent's or worker's is named after its task
    /// id, as its lifecycle events name it. Anything else on a run is the turn's own progress; on
    /// the notify stream it is the primary agent's (a run it timed out on) unless another agent
    /// is named.
    /// </summary>
    private (MessageCategory Category, string? AgentName) Source(string? subagentRunId, string? agentName)
    {
        if (!string.IsNullOrEmpty(subagentRunId))
            return (MessageCategory.SubagentActivity, SubagentName(subagentRunId));

        if (agentName?.StartsWith("subagent-", StringComparison.OrdinalIgnoreCase) == true
            || agentName?.StartsWith("worker-", StringComparison.OrdinalIgnoreCase) == true)
            return (MessageCategory.SubagentActivity, agentName);

        if (IsRun || string.IsNullOrEmpty(agentName) || PrimaryAgentName is null
            || string.Equals(agentName, PrimaryAgentName, StringComparison.OrdinalIgnoreCase))
            return (MessageCategory.PrimaryProgress, null);

        return (MessageCategory.A2AActivity, agentName);
    }

    private string SubagentName(string taskId) =>
        ChatReducer.SubagentAgentName(_subagentKinds.GetValueOrDefault(taskId), taskId);

    private string? SubagentName(ReplySource? source, string? subagentRunId)
    {
        if (source is { Kind: ReplySourceKind.Subagent or ReplySourceKind.Worker, TaskId: { Length: > 0 } taskId })
        {
            _subagentKinds[taskId] = source.Kind;
            return ChatReducer.SubagentAgentName(source.Kind, taskId);
        }

        return string.IsNullOrEmpty(subagentRunId) ? null : SubagentName(subagentRunId);
    }

    // ── Task lists ───────────────────────────────────────────────────────────

    private void MapStateSnapshot(StateSnapshotEvent state, List<ChatEvent> events)
    {
        if (Read(state.Snapshot, WireJson.Default.StateDocument)?.TaskLists is not { } lists)
            return;

        foreach (var (key, items) in lists)
        {
            events.Add(Stamp(new TaskListSnapshotReceived(new TaskListSnapshot
            {
                ThreadId = _threadId, RunId = TaskListRunId(key), Source = TaskListSource(key), Items = items,
            }), state));
        }
    }

    private void MapStateDelta(StateDeltaEvent state, List<ChatEvent> events)
    {
        // The proxy's patches are always one item added to, or replaced in, /taskLists/{key}. The
        // chat state keys items by id, so the item and its list are all it needs: no document to
        // keep and patch.
        if (Read(state.Delta, WireJson.Default.ListPatchOperation) is not { } operations)
            return;

        foreach (var operation in operations)
        {
            if (operation is not { Op: "add" or "replace", Path: { } path, Value: { } value }
                || path.Split('/') is not ["", "taskLists", var escapedKey, _]
                || Read(value, WireJson.Default.TaskListItem) is not { } item)
                continue;

            // RFC 6901: "~1" is "/", "~0" is "~".
            var key = escapedKey.Replace("~1", "/").Replace("~0", "~");
            events.Add(Stamp(new TaskListDeltaReceived(new TaskListDelta
            {
                ThreadId = _threadId, RunId = TaskListRunId(key), Source = TaskListSource(key), Item = item,
            }), state));
        }
    }

    private ReplySource TaskListSource(string key) =>
        key == RockBotEvents.PrimaryTaskListKey
            ? ReplySource.Primary
            : _subagentKinds.GetValueOrDefault(key) == ReplySourceKind.Worker ? ReplySource.Worker(key) : ReplySource.Subagent(key);

    private string? TaskListRunId(string key) =>
        key == RockBotEvents.PrimaryTaskListKey ? _runId ?? LateRunId : null;

    // ── Subagents ────────────────────────────────────────────────────────────

    private void MapSubagentStarted(SubagentStartedEvent started, List<ChatEvent> events)
    {
        // rawEvent is RockBot's own record, with the kind and the start time.
        var record = Read(started.RawEvent, WireJson.Default.SubagentStarted) ?? new SubagentStarted
        {
            SubagentRunId = started.SubagentRunId ?? "",
            Kind = started.Name is ReplySourceKind.Worker ? ReplySourceKind.Worker : ReplySourceKind.Subagent,
            Description = started.Description ?? "",
            ThreadId = _threadId,
            ParentToolCallId = started.ParentToolCallId,
            StartedAt = At(started),
        };

        _subagentKinds[record.SubagentRunId] = record.Kind;
        _openActivities.Add(ChatReducer.SubagentAgentName(record.Kind, record.SubagentRunId));
        events.Add(Stamp(new SubagentRunStarted(record), started));
    }

    private void MapSubagentFinished(BaseEvent e, string subagentRunId, string outcome, string? error, List<ChatEvent> events)
    {
        var record = Read(e.RawEvent, WireJson.Default.SubagentFinished) ?? new SubagentFinished
        {
            SubagentRunId = subagentRunId,
            Kind = _subagentKinds.GetValueOrDefault(subagentRunId) ?? ReplySourceKind.Subagent,
            ThreadId = _threadId,
            Outcome = outcome,
            Error = error,
            FinishedAt = At(e),
        };

        _openActivities.Remove(ChatReducer.SubagentAgentName(record.Kind, record.SubagentRunId));
        events.Add(Stamp(new SubagentRunFinished(record), e));
    }

    // ── CUSTOM rockbot.* ─────────────────────────────────────────────────────

    private void MapCustom(CustomEvent custom, List<ChatEvent> events)
    {
        switch (custom.Name)
        {
            case RockBotEvents.Attachments:
                if (Read(custom.Value, WireJson.Default.AttachmentsValue) is { Attachments: { Count: > 0 } attachments } value)
                {
                    foreach (var attachment in attachments.Where(a => !string.IsNullOrEmpty(a.Url)))
                        _attachmentUrls[attachment.Path] = attachment.Url!;
                    if (_pending is not null && _pending.Id == value.MessageId)
                        _pending.Attachments = [.. attachments.Select(a => a.ToAttachment())];
                }
                break;

            case RockBotEvents.Origin:
                if (Read(custom.Value, WireJson.Default.OriginValue) is { } origin
                    && _pending is not null && _pending.Id == origin.MessageId)
                    events.Add(Unsolicited(TakePending()!, origin, custom));
                break;

            case RockBotEvents.McpQuestion:
                if (Question(custom.Value) is { } question)
                    events.Add(Stamp(question, custom));
                break;

            case RockBotEvents.McpQuestionStatus:
                if (Read(custom.Value, WireJson.Default.McpQuestionStatus) is { } status)
                    events.Add(Stamp(new McpQuestionStatusChanged(status), custom));
                break;

            case RockBotEvents.ReplyPending:
                ReplyPending = true;
                break;

            // The agent started on the run, or is still at it. In a run the id is the run's own;
            // on the notify stream it is the run the client stopped waiting for.
            case RockBotEvents.AgentRunStarted:
            case RockBotEvents.Heartbeat:
                var run = Read(custom.Value, WireJson.Default.RunValue);
                if ((_runId ?? run?.RunId) is { } turnId)
                {
                    if (!IsRun)
                        LateRunId = turnId;
                    events.Add(Stamp(new TurnHeartbeat(turnId, run?.Status), custom));
                }
                break;

            case RockBotEvents.RunFinished:
                if (Read(custom.Value, WireJson.Default.RunValue) is { RunId: { } finishedRun } finished)
                {
                    if (LateRunId == finishedRun)
                        LateRunId = null;
                    events.Add(Stamp(new LateRunFinished(finishedRun, finished.Outcome ?? ""), custom));
                }
                break;

            case RockBotEvents.AgentName:
                if (Read(custom.Value, WireJson.Default.NameValue)?.AgentName is { Length: > 0 } name)
                {
                    PrimaryAgentName = name;
                    events.Add(Stamp(new AgentInfoReceived(name, AgentVersion), custom));
                }
                break;

            case RockBotEvents.Error:
                if (Read(custom.Value, WireJson.Default.NameValue)?.Message is { Length: > 0 } message)
                    events.Add(Stamp(new ErrorReported(message), custom));
                break;

            case RockBotEvents.ActiveWork:
                // The agent broadcasts this when it starts: whatever was running before is gone.
                var work = Read(custom.Value, WireJson.Default.ActiveWorkValue)?.Work ?? [];
                _openActivities.Clear();
                events.Add(Stamp(new ActiveStatusReconciled(new ActiveStatusResponse
                {
                    Subagents = [], IsProcessing = false, Work = work,
                }), custom));
                break;

            case RockBotEvents.ContextCleared:
                Reset();
                events.Add(Stamp(new ConversationCleared(), custom));
                break;

            // rockbot.connected and rockbot.resync are about the connection: the reader handles
            // them. rockbot.mcp_signin_status: MCP sign-in is not part of this sample.
        }
    }

    /// <summary>
    /// A question, from the value of a <c>rockbot.mcp_question</c> event or an entry of
    /// <c>GET /threads/{id}/questions</c> (they are the same object).
    /// </summary>
    public static McpQuestionReceived? Question(JsonElement? value)
    {
        if (Read(value, WireJson.Default.QuestionValue) is not { } question)
            return null;

        var form = question.Form is { ValueKind: JsonValueKind.Object } json
            ? Read(json, WireJson.Default.McpQuestionForm)
            : null;

        return new McpQuestionReceived(new AgentReply
        {
            Content = question.Text ?? form?.Message ?? "",
            SessionId = question.SessionId ?? "",
            AgentName = question.AgentName ?? "",
            IsFinal = true,
            ContentType = McpQuestionForm.ContentType,
            StructuredData = question.Form?.GetRawText(),
        }, form is { QuestionId.Length: > 0 } ? form : null);
    }

    /// <summary>
    /// The conversation as <c>GET /threads/{id}/messages</c> returns it (one
    /// <c>MESSAGES_SNAPSHOT</c>), as the event that replaces the transcript. Attachment URLs go to
    /// <paramref name="attachmentUrls"/>, by path. A turn the agent recorded an origin for gets the
    /// anchor its live copy had (#809), seen from <paramref name="channel"/> at <paramref name="clock"/>'s now.
    /// </summary>
    public static HistoryLoaded History(JsonElement snapshot, string threadId, IDictionary<string, string>? attachmentUrls = null,
        TimeProvider? clock = null, string channel = "agui")
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var turns = new List<ConversationHistoryTurn>();
        foreach (var message in Read(snapshot, WireJson.Default.HistorySnapshot)?.Messages ?? [])
        {
            var detail = message.Metadata?.Rockbot;
            foreach (var attachment in detail?.Attachments ?? [])
            {
                if (attachmentUrls is not null && !string.IsNullOrEmpty(attachment.Url))
                    attachmentUrls[attachment.Path] = attachment.Url!;
            }

            // A message's content is text here; AG-UI also allows a list of parts, which the proxy
            // never sends.
            var content = message.Content is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? "" : "";
            var sessionId = detail?.SessionId ?? threadId;
            turns.Add(new ConversationHistoryTurn
            {
                Role = message.Role ?? "assistant",
                Content = ReplyOriginFormatter.WithQuotedAnchor(content, detail?.Origin, channel, sessionId, now),
                Timestamp = detail?.Timestamp ?? default,
                AgentName = detail?.AgentName ?? message.Name,
                Attachments = detail?.Attachments is { Count: > 0 } attachments
                    ? [.. attachments.Select(a => a.ToAttachment())]
                    : null,
                // The proxy's message id is the turn's own, which its live copy also had.
                Id = message.Id,
                SessionId = detail?.SessionId,
                Origin = detail?.Origin,
                Source = detail?.Source,
            });
        }

        return new HistoryLoaded(turns, threadId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static T? Read<T>(JsonElement? json, JsonTypeInfo<T> type) where T : class
    {
        if (json is not { ValueKind: JsonValueKind.Object or JsonValueKind.Array } element)
            return null;
        try
        {
            return element.Deserialize(type);
        }
        catch (JsonException)
        {
            // A payload this client doesn't understand is skipped, like an unknown event.
            return null;
        }
    }

    private static T? Read<T>(string? json, JsonTypeInfo<T> type) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private DateTimeOffset At(BaseEvent e) =>
        e.Timestamp is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : _clock.GetUtcNow();

    /// <summary>
    /// Gives a chat event the time its AG-UI event carries, so a recorded stream always maps to
    /// the same chat events. Without one the store stamps it as it is applied.
    /// </summary>
    private static T Stamp<T>(T chatEvent, BaseEvent e, long? fallback = null) where T : ChatEvent =>
        (e.Timestamp ?? fallback) is { } ms
            ? chatEvent with { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime }
            : chatEvent;

    private sealed record ToolCall(string Name, MessageCategory Category, string? AgentName);

    private sealed class PendingMessage(string id, string? name, string? subagentRunId, long? timestamp)
    {
        public string Id => id;
        public string? Name => name;
        public string? SubagentRunId => subagentRunId;
        public long? Timestamp => timestamp;
        public StringBuilder Text { get; } = new();
        public IReadOnlyList<AgentAttachment>? Attachments { get; set; }
        public bool Ended { get; set; }
    }
}
