using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using RockBot.UserProxy.Rendering;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Translates one turn's RockBot bus traffic into AG-UI 1.0 events (#797). It is the same mapping
/// the AG-UI proxy (#794, <c>design/userproxy-agui.md</c>) needs, exercised first by
/// <c>rockbot chat --output jsonl</c>.
/// </summary>
/// <remarks>
/// Keeps the turn's state (activity ids, task lists), so it is not thread-safe: the caller
/// serializes calls (<see cref="JsonLinesTurnWriter"/> does, under its lock). AG-UI has no field
/// for some RockBot detail, such as tool-call durations and failures; that goes in the event's
/// <c>metadata.rockbot</c> object, and the RockBot record it came from goes in <c>rawEvent</c>.
/// </remarks>
internal sealed class TurnEventMapper(string threadId, string runId, TimeProvider? clock = null)
{
    /// <summary><c>CUSTOM</c> event names this mapper emits.</summary>
    public static class CustomNames
    {
        public const string AttachmentSent = "rockbot.attachment_sent";
        public const string AgentRunStarted = "rockbot.agent_run_started";
        public const string Heartbeat = "rockbot.heartbeat";
        public const string Attachments = "rockbot.attachments";
        public const string Origin = "rockbot.origin";
    }

    /// <summary><c>RUN_ERROR</c> codes this mapper emits.</summary>
    public static class ErrorCodes
    {
        public const string Error = "error";
        public const string Timeout = "timeout";
        public const string AgentLost = "agent_lost";
        public const string AttachmentFailed = "attachment_failed";
        public const string ClientError = "client_error";
    }

    /// <summary>The <c>ACTIVITY_SNAPSHOT</c> activity type for progress text.</summary>
    public const string ProgressActivityType = "rockbot.progress";

    /// <summary>The task-list key of the primary turn in the <c>STATE_*</c> state.</summary>
    public const string PrimaryTaskListKey = "primary";

    /// <summary>RockBot payloads (<c>rawEvent</c>, custom values) use the bus's camelCase JSON.</summary>
    internal static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, List<TaskListItem>> _taskLists = new(StringComparer.Ordinal);

    public string ThreadId => threadId;
    public string RunId => runId;

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
    public IReadOnlyList<BaseEvent> Progress(AgentReply reply)
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
                            Metadata = RockBotMetadata(new
                            {
                                status = call.Phase,
                                failed,
                                call.DurationMs,
                                call.Error,
                                reply.AgentName,
                            }),
                            RawEvent = ToElement(call),
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
                MessageId = subagentRunId is null ? $"{runId}-progress" : $"{runId}-progress-{subagentRunId}",
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

    /// <summary>A task list was created or replaced: the whole state, every list included.</summary>
    public BaseEvent TaskListSnapshot(TaskListSnapshot snapshot)
    {
        _taskLists[TaskListKey(snapshot.Source)] = [.. snapshot.Items];
        return Stamp(new StateSnapshotEvent { Snapshot = StateDocument() });
    }

    /// <summary>One task-list item changed: an RFC 6902 patch against the state.</summary>
    public BaseEvent TaskListDelta(TaskListDelta delta)
    {
        var key = TaskListKey(delta.Source);
        var listPath = $"/taskLists/{EscapePointer(key)}";
        var item = ToNode(delta.Item);
        JsonObject op;

        if (!_taskLists.TryGetValue(key, out var list))
        {
            // A delta for a list we never saw: create the list with this item.
            _taskLists[key] = [delta.Item];
            op = new JsonObject { ["op"] = "add", ["path"] = listPath, ["value"] = new JsonArray(item) };
        }
        else if (list.FindIndex(t => t.Id == delta.Item.Id) is var index and >= 0)
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
        events.Add(Custom(CustomNames.Origin, new { messageId, reply.Origin, reply.Source }));
        return events;
    }

    /// <summary>
    /// The turn's end: its final reply when one arrived, then <c>RUN_FINISHED</c> or
    /// <c>RUN_ERROR</c>.
    /// </summary>
    public IReadOnlyList<BaseEvent> Finish(TurnResult result)
    {
        var events = new List<BaseEvent>();
        if (result.Reply is { } reply)
            events.AddRange(TextMessage(reply).Events);

        events.Add(result.Outcome switch
        {
            TurnResultOutcome.Success => Stamp(new RunFinishedEvent { ThreadId = threadId, RunId = runId }),
            TurnResultOutcome.Cancelled => Stamp(new RunFinishedEvent
            {
                ThreadId = threadId, RunId = runId, Outcome = new RunFinishedCancelledOutcome(),
            }),
            TurnResultOutcome.Error => RunError(ErrorCodes.Error, ChatExitCodes.Describe(result)!),
            TurnResultOutcome.AgentLost => RunError(ErrorCodes.AgentLost, ChatExitCodes.Describe(result)!),
            _ => RunError(ErrorCodes.Timeout, ChatExitCodes.Describe(result)!),
        });
        return events;
    }

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
            events.Add(Custom(CustomNames.Attachments, new { messageId, attachments }));
        return (messageId, events);
    }

    private BaseEvent Custom(string name, object value, DateTimeOffset? at = null) =>
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
