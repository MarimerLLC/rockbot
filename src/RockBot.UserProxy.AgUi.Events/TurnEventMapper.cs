using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using RockBot.UserProxy.Rendering;

namespace RockBot.UserProxy.AgUi;

/// <summary>
/// Translates one turn's RockBot bus traffic into AG-UI 1.0 events (#797). One mapping serves
/// both <c>rockbot chat --output jsonl</c> and the AG-UI proxy (#794,
/// <c>design/userproxy-agui.md</c>).
/// </summary>
/// <remarks>
/// Keeps the turn's state (activity ids, task lists), so it is not thread-safe: the caller
/// serializes calls (<see cref="TurnEventEmitter"/> does, under its lock). AG-UI has no field
/// for some RockBot detail, such as tool-call durations and failures; that goes in the event's
/// <c>metadata.rockbot</c> object. Subagent lifecycle events also carry the RockBot record they
/// came from in <c>rawEvent</c>, for its kind, thread and times.
/// </remarks>
public sealed class TurnEventMapper(string threadId, string runId, TimeProvider? clock = null)
{
    /// <summary><c>CUSTOM</c> event names this mapper emits.</summary>
    public static class CustomNames
    {
        public const string AttachmentSent = "rockbot.attachment_sent";
        public const string AgentRunStarted = "rockbot.agent_run_started";
        public const string Heartbeat = "rockbot.heartbeat";
        public const string Attachments = "rockbot.attachments";
        public const string Origin = "rockbot.origin";

        /// <summary>A run ended without its reply; the reply will arrive on the notify stream.</summary>
        public const string ReplyPending = "rockbot.reply_pending";

        // The rest are only emitted by the AG-UI proxy (#794).

        /// <summary>An MCP server's question as a form: on the notify stream, or in a run.</summary>
        public const string McpQuestion = "rockbot.mcp_question";
        public const string McpQuestionStatus = "rockbot.mcp_question_status";

        // Only on the proxy's notify stream.
        public const string RunFinished = "rockbot.run_finished";
        public const string McpSignInStatus = "rockbot.mcp_signin_status";
        public const string AgentName = "rockbot.agent_name";
        public const string Error = "rockbot.error";
        public const string ActiveWork = "rockbot.active_work";

        /// <summary>The thread's conversation context was cleared: drop what is shown of it.</summary>
        public const string ContextCleared = "rockbot.context_cleared";
        public const string Connected = "rockbot.connected";
        public const string Resync = "rockbot.resync";
    }

    /// <summary><c>RUN_ERROR</c> codes this mapper emits.</summary>
    public static class ErrorCodes
    {
        public const string Error = "error";
        public const string Timeout = "timeout";
        public const string AgentLost = "agent_lost";
        public const string AttachmentFailed = "attachment_failed";
        public const string ClientError = "client_error";

        // The AG-UI proxy's interrupt protocol (#794).

        /// <summary>New input was sent while the thread has interrupts to answer.</summary>
        public const string InterruptsPending = "interrupts_pending";

        /// <summary>A run's <c>resume</c> doesn't answer every open interrupt.</summary>
        public const string ResumeIncomplete = "resume_incomplete";

        /// <summary>The agent refused an answer; the interrupt is still open and can be answered again.</summary>
        public const string InterruptRejected = "interrupt_rejected";
    }

    /// <summary>The <c>ACTIVITY_SNAPSHOT</c> activity type for progress text.</summary>
    public const string ProgressActivityType = "rockbot.progress";

    /// <summary>The task-list key of the primary turn in the <c>STATE_*</c> state.</summary>
    public const string PrimaryTaskListKey = "primary";

    /// <summary>RockBot payloads (<c>rawEvent</c>, custom values) use the bus's camelCase JSON.</summary>
    public static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, List<TaskListItem>> _taskLists = new(StringComparer.Ordinal);

    public string ThreadId => threadId;
    public string RunId => runId;

    /// <summary>
    /// When true, a task-list change is always a <c>STATE_SNAPSHOT</c>, never a <c>STATE_DELTA</c>.
    /// For a stream a consumer may join part-way (the proxy's notify stream), where a patch could
    /// refer to a list the consumer was never given.
    /// </summary>
    public bool AlwaysSnapshotState { get; init; }

    /// <summary>
    /// Where a consumer can fetch an attachment's bytes, for a host that serves them (the AG-UI
    /// proxy). When set, each entry of <c>rockbot.attachments</c> also has a <c>url</c>. The CLI
    /// serves nothing and leaves it unset, so its entries are the attachment alone.
    /// </summary>
    public Func<AgentAttachment, string?>? AttachmentUrl { get; init; }

    public BaseEvent RunStarted() => Stamp(new RunStartedEvent
    {
        ThreadId = threadId,
        RunId = runId,
        ProtocolVersion = AGUIProtocol.Version,
    });

    public BaseEvent AttachmentSent(AgentAttachment attachment) =>
        Custom(CustomNames.AttachmentSent, new { attachment });

    public BaseEvent AgentRunStarted(RunStarted run) =>
        Custom(CustomNames.AgentRunStarted, new { run.AgentName, run.HeartbeatIntervalSeconds }, run.Timestamp);

    public BaseEvent Heartbeat(RunHeartbeat heartbeat) =>
        Custom(CustomNames.Heartbeat, new { heartbeat.ElapsedSeconds, heartbeat.Status }, heartbeat.Timestamp);

    /// <summary>
    /// A non-final reply: tool-call events when it carries a <see cref="ToolCallInfo"/>, else an
    /// activity snapshot with its text. A subagent's or worker's reply is attributed to it.
    /// </summary>
    public IReadOnlyList<BaseEvent> Progress(AgentReply reply) => Progress(reply, activityId: null);

    /// <summary>
    /// As <see cref="Progress(AgentReply)"/>, with the activity's id chosen by the caller. Without
    /// one, the id is derived from the run and the subagent.
    /// </summary>
    public IReadOnlyList<BaseEvent> Progress(AgentReply reply, string? activityId)
    {
        var subagentRunId = SubagentRunId(reply.Source);

        if (reply.ToolCall is { } call)
        {
            switch (call.Phase)
            {
                case ToolCallPhase.Started:
                {
                    var events = new List<BaseEvent>(3)
                    {
                        Stamp(new ToolCallStartEvent
                        {
                            ToolCallId = call.ToolCallId,
                            ToolCallName = call.ToolCallName,
                            SubagentRunId = subagentRunId,
                        }),
                    };
                    // RockBot carries a readable summary rather than the JSON arguments; wrap it so
                    // the call's argument text is still a JSON document.
                    if (!string.IsNullOrEmpty(call.ArgsSummary))
                    {
                        events.Add(Stamp(new ToolCallArgsEvent
                        {
                            ToolCallId = call.ToolCallId,
                            Delta = JsonSerializer.Serialize(new { summary = call.ArgsSummary }, PayloadJson),
                            SubagentRunId = subagentRunId,
                        }));
                    }
                    events.Add(Stamp(new ToolCallEndEvent { ToolCallId = call.ToolCallId, SubagentRunId = subagentRunId }));
                    return events;
                }

                case ToolCallPhase.Finished:
                case ToolCallPhase.Failed:
                {
                    var failed = call.Phase == ToolCallPhase.Failed;
                    var content = failed ? call.Error ?? "unknown error" : call.Result ?? "";
                    return
                    [
                        Stamp(new ToolCallResultEvent
                        {
                            MessageId = NewId(),
                            ToolCallId = call.ToolCallId,
                            Content = content,
                            Role = "tool",
                            SubagentRunId = subagentRunId,
                            // content already holds the result or the error; this adds only what
                            // AG-UI has no field for.
                            Metadata = RockBotMetadata(new
                            {
                                status = call.Phase,
                                failed,
                                call.DurationMs,
                                reply.AgentName,
                            }),
                        }),
                    ];
                }
            }
        }

        return
        [
            Stamp(new ActivitySnapshotEvent
            {
                // One activity per source, so a subagent's progress doesn't overwrite the turn's.
                MessageId = activityId ?? (subagentRunId is null ? $"{runId}-progress" : $"{runId}-progress-{subagentRunId}"),
                ActivityType = ProgressActivityType,
                Content = ToElement(new
                {
                    reply.AgentName,
                    text = HtmlPlainTextRenderer.StripHtml(reply.Content),
                }),
                Replace = true,
                SubagentRunId = subagentRunId,
            }),
        ];
    }

    /// <summary>
    /// The source of the activity <paramref name="activityId"/> has finished: a last snapshot with
    /// <c>content.done</c> set and no text, so a consumer can take its indicator down.
    /// </summary>
    public BaseEvent ActivityDone(AgentReply reply, string activityId) => Stamp(new ActivitySnapshotEvent
    {
        MessageId = activityId,
        ActivityType = ProgressActivityType,
        Content = ToElement(new { reply.AgentName, text = "", done = true }),
        Replace = true,
        SubagentRunId = SubagentRunId(reply.Source),
    });

    /// <summary>A task list was created or replaced: the whole state, every list included.</summary>
    public BaseEvent TaskListSnapshot(TaskListSnapshot snapshot)
    {
        _taskLists[TaskListKey(snapshot.Source)] = [.. snapshot.Items];
        return Stamp(new StateSnapshotEvent { Snapshot = StateDocument() });
    }

    /// <summary>
    /// One task-list item changed: an RFC 6902 patch against the state. For a list the stream has
    /// not shown yet, a snapshot instead: a patch would need a parent (<c>/taskLists</c>, or the
    /// list) the consumer may never have been given.
    /// </summary>
    public BaseEvent TaskListDelta(TaskListDelta delta)
    {
        var key = TaskListKey(delta.Source);
        if (!_taskLists.TryGetValue(key, out var list))
        {
            _taskLists[key] = [delta.Item];
            return Stamp(new StateSnapshotEvent { Snapshot = StateDocument() });
        }

        if (AlwaysSnapshotState)
        {
            if (list.FindIndex(t => t.Id == delta.Item.Id) is var at and >= 0)
                list[at] = delta.Item;
            else
                list.Add(delta.Item);
            return Stamp(new StateSnapshotEvent { Snapshot = StateDocument() });
        }

        var listPath = $"/taskLists/{EscapePointer(key)}";
        var item = ToNode(delta.Item);
        JsonObject op;

        if (list.FindIndex(t => t.Id == delta.Item.Id) is var index and >= 0)
        {
            list[index] = delta.Item;
            op = new JsonObject { ["op"] = "replace", ["path"] = $"{listPath}/{index}", ["value"] = item };
        }
        else
        {
            list.Add(delta.Item);
            op = new JsonObject { ["op"] = "add", ["path"] = $"{listPath}/-", ["value"] = item };
        }

        return Stamp(new StateDeltaEvent { Delta = ToElement(new JsonArray(op)) });
    }

    /// <summary>
    /// Drops a finished subagent's or worker's task list from the state, so a mapper that lives
    /// longer than one turn doesn't keep every list it has ever seen. Returns the state without
    /// it, or null when there was no such list.
    /// </summary>
    public BaseEvent? ForgetTaskList(string subagentRunId) =>
        _taskLists.Remove(subagentRunId) ? Stamp(new StateSnapshotEvent { Snapshot = StateDocument() }) : null;

    public BaseEvent SubagentStarted(SubagentStarted started) => Stamp(new SubagentStartedEvent
    {
        SubagentRunId = started.SubagentRunId,
        Name = started.Kind,
        Description = started.Description,
        ParentToolCallId = started.ParentToolCallId,
        RawEvent = ToElement(started),
    }, started.StartedAt);

    public BaseEvent SubagentFinished(SubagentFinished finished) =>
        finished.Outcome == SubagentOutcome.Success
            ? Stamp(new SubagentFinishedEvent { SubagentRunId = finished.SubagentRunId, RawEvent = ToElement(finished) },
                finished.FinishedAt)
            : Stamp(new SubagentErrorEvent
            {
                SubagentRunId = finished.SubagentRunId,
                Code = finished.Outcome,
                Message = finished.Error ?? $"The {finished.Kind} ended: {finished.Outcome}.",
                RawEvent = ToElement(finished),
            }, finished.FinishedAt);

    /// <summary>
    /// A final reply that isn't this turn's (a subagent result, a scheduled task): the text
    /// message, then <c>rockbot.origin</c> saying where it came from.
    /// </summary>
    public IReadOnlyList<BaseEvent> UnsolicitedReply(AgentReply reply)
    {
        var (messageId, events) = TextMessage(reply);
        // followsQuestionId: set on the agent's reply to a form answer, so a consumer can pair them.
        events.Add(Custom(CustomNames.Origin, new { messageId, reply.Origin, reply.Source, reply.FollowsQuestionId }));
        return events;
    }

    /// <summary>
    /// The turn's end: its final reply when one arrived, then <c>RUN_FINISHED</c> or
    /// <c>RUN_ERROR</c>. A successful run that leaves <paramref name="interrupts"/> open ends with
    /// AG-UI's interrupt outcome instead of plain success: the client answers them with a run
    /// that carries <c>resume</c>.
    /// </summary>
    public IReadOnlyList<BaseEvent> Finish(TurnResult result, IReadOnlyList<AGUIInterrupt>? interrupts = null)
    {
        var events = new List<BaseEvent>();
        if (result.Reply is { } reply)
            events.AddRange(TextMessage(reply).Events);

        events.Add(result.Outcome switch
        {
            TurnResultOutcome.Success => RunFinished(interrupts),
            TurnResultOutcome.Cancelled => Stamp(new RunFinishedEvent
            {
                ThreadId = threadId, RunId = runId, Outcome = new RunFinishedCancelledOutcome(),
            }),
            TurnResultOutcome.Error => RunError(ErrorCodes.Error, TurnResultText.Describe(result)!),
            TurnResultOutcome.AgentLost => RunError(ErrorCodes.AgentLost, TurnResultText.Describe(result)!),
            _ => RunError(ErrorCodes.Timeout, TurnResultText.Describe(result)!),
        });
        return events;
    }

    /// <summary>
    /// The end of a run the proxy stopped waiting for while the agent may still be working: a
    /// <c>rockbot.reply_pending</c> event, then <c>RUN_FINISHED</c> (plain, or with the interrupt
    /// outcome when <paramref name="interrupts"/> are open). The reply, if one comes, is delivered
    /// some other way (the AG-UI proxy's notify stream). Only for a host that has such a way;
    /// <see cref="Finish"/> reports the same turn as <c>RUN_ERROR</c>.
    /// </summary>
    public IReadOnlyList<BaseEvent> ReplyPending(TurnResult result, IReadOnlyList<AGUIInterrupt>? interrupts = null) =>
    [
        Custom(CustomNames.ReplyPending, new
        {
            runId,
            result.RunStarted,
            message = result.Error is { Length: > 0 } error ? error : TurnResultText.Describe(result),
        }),
        RunFinished(interrupts),
    ];

    /// <summary><c>RUN_FINISHED</c>: plain success, or the interrupt outcome when something is left to answer.</summary>
    private BaseEvent RunFinished(IReadOnlyList<AGUIInterrupt>? interrupts) => Stamp(new RunFinishedEvent
    {
        ThreadId = threadId,
        RunId = runId,
        // The outcome needs at least one interrupt. None is an ordinary success: an absent outcome.
        Outcome = interrupts is { Count: > 0 } ? new RunFinishedInterruptOutcome { Interrupts = [.. interrupts] } : null,
    });

    public BaseEvent RunError(string code, string message) =>
        Stamp(new RunErrorEvent { Code = code, Message = message });

    /// <summary>True for the events that end a run's stream.</summary>
    public static bool IsTerminal(BaseEvent e) => e is RunFinishedEvent or RunErrorEvent;

    private (string MessageId, List<BaseEvent> Events) TextMessage(AgentReply reply)
    {
        var messageId = NewId();
        var subagentRunId = SubagentRunId(reply.Source);
        var events = new List<BaseEvent>
        {
            Stamp(new TextMessageStartEvent
            {
                MessageId = messageId, Role = "assistant", Name = reply.AgentName, SubagentRunId = subagentRunId,
            }),
            Stamp(new TextMessageContentEvent { MessageId = messageId, Delta = reply.Content, SubagentRunId = subagentRunId }),
            Stamp(new TextMessageEndEvent { MessageId = messageId, SubagentRunId = subagentRunId }),
        };
        if (reply.Attachments is { Count: > 0 } attachments)
            events.Add(Custom(CustomNames.Attachments, new { messageId, attachments = WithUrls(attachments) }));
        return (messageId, events);
    }

    /// <summary>
    /// The attachments as the events and history list them: each as it is on the bus, plus a
    /// <c>url</c> when this host serves attachment bytes (<see cref="AttachmentUrl"/>).
    /// </summary>
    public object WithUrls(IReadOnlyList<AgentAttachment> attachments) =>
        AttachmentUrl is not { } url
            ? attachments
            : attachments.Select(a => new { a.Mime, a.Path, a.FileName, Url = url(a) }).ToList();

    /// <summary>A <c>CUSTOM</c> event whose value is <paramref name="value"/> as camelCase JSON.</summary>
    public BaseEvent Custom(string name, object value, DateTimeOffset? at = null) =>
        Stamp(new CustomEvent { Name = name, Value = ToElement(value) }, at);

    private BaseEvent Stamp(BaseEvent e, DateTimeOffset? at = null)
    {
        e.Timestamp = (at ?? _clock.GetUtcNow()).ToUnixTimeMilliseconds();
        return e;
    }

    private JsonElement StateDocument()
    {
        var lists = new JsonObject();
        foreach (var (key, items) in _taskLists)
            lists[key] = new JsonArray([.. items.Select(ToNode)]);
        return ToElement(new JsonObject { ["taskLists"] = lists });
    }

    private static string? SubagentRunId(ReplySource? source) =>
        source is { Kind: ReplySourceKind.Subagent or ReplySourceKind.Worker, TaskId: { Length: > 0 } id } ? id : null;

    private static string TaskListKey(ReplySource source) => SubagentRunId(source) ?? PrimaryTaskListKey;

    /// <summary>RFC 6901 escaping for one JSON Pointer segment.</summary>
    private static string EscapePointer(string segment) => segment.Replace("~", "~0").Replace("/", "~1");

    private static JsonElement RockBotMetadata(object value) => ToElement(new { rockbot = value });

    private static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, PayloadJson);

    private static JsonNode? ToNode(TaskListItem item) => JsonSerializer.SerializeToNode(item, PayloadJson);

    private static string NewId() => Guid.NewGuid().ToString("N");
}
