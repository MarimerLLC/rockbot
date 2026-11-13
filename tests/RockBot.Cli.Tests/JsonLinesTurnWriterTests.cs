using System.Text.Json;
using AGUI.Abstractions;
using RockBot.UserProxy;
using RockBot.UserProxy.Cli;

namespace RockBot.Cli.Tests;

/// <summary>
/// Tests for <c>rockbot chat --output jsonl</c> (#797): the turn's bus traffic as AG-UI 1.0
/// events, one JSON object per line. Assertions read the parsed JSON, not exact strings.
/// </summary>
[TestClass]
public sealed class JsonLinesTurnWriterTests
{
    private const string Session = "cli-session";
    private const string Run = "run-1";

    private StringWriter _out = null!;
    private JsonLinesTurnWriter _writer = null!;

    [TestInitialize]
    public void Init()
    {
        _out = new StringWriter();
        _writer = new JsonLinesTurnWriter(_out, Session, Run);
    }

    private List<JsonElement> Lines() =>
        _out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();

    private static string Type(JsonElement e) => e.GetProperty("type").GetString()!;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.GetString() : null;

    private static AgentReply Progress(string content, ToolCallInfo? call = null, ReplySource? source = null) => new()
    {
        Content = content, SessionId = Session, AgentName = "RockBot", IsFinal = false, ToolCall = call, Source = source,
    };

    private static AgentReply Final(string content, IReadOnlyList<AgentAttachment>? attachments = null) => new()
    {
        Content = content, SessionId = Session, AgentName = "RockBot", Attachments = attachments,
    };

    private static TaskListItem Item(string id, string status) => new() { Id = id, Description = $"step {id}", Status = status };

    [TestMethod]
    public void SuccessfulTurn_StartsWithRunStarted_EndsWithRunFinished()
    {
        _writer.Start();
        _writer.Finish(new TurnResult { Outcome = TurnResultOutcome.Success, CorrelationId = Run, Reply = Final("Hello **there**") });

        var lines = Lines();
        CollectionAssert.AreEqual(
            new[] { "RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED" },
            lines.Select(Type).ToArray());

        Assert.AreEqual(Session, Str(lines[0], "threadId"));
        Assert.AreEqual(Run, Str(lines[0], "runId"));
        Assert.AreEqual(AGUIProtocol.Version, Str(lines[0], "protocolVersion"));
        Assert.AreEqual("assistant", Str(lines[1], "role"));
        Assert.AreEqual("Hello **there**", Str(lines[2], "delta"));
        Assert.AreEqual(Str(lines[1], "messageId"), Str(lines[2], "messageId"));
        Assert.AreEqual(Run, Str(lines[4], "runId"));
        Assert.IsFalse(lines[4].TryGetProperty("outcome", out _), "success is an absent outcome");
        Assert.IsTrue(lines.All(l => l.TryGetProperty("timestamp", out _)));
    }

    [TestMethod]
    public void EveryLine_ParsesBackAsItsAgUiEventType()
    {
        _writer.Start();
        _writer.Report(Progress("Thinking"));
        _writer.Report(Progress("Calling web_search…", new ToolCallInfo
        {
            ToolCallId = "c1", ToolCallName = "web_search", Phase = ToolCallPhase.Started, ArgsSummary = "query: rockbot",
        }));
        _writer.OnTaskListSnapshot(new TaskListSnapshot { ThreadId = Session, Source = ReplySource.Primary, Items = [Item("1", "pending")] });
        _writer.OnRunHeartbeat(new RunHeartbeat { ThreadId = Session, RunId = Run, Timestamp = DateTimeOffset.UtcNow, ElapsedSeconds = 5 });
        _writer.Finish(new TurnResult { Outcome = TurnResultOutcome.Cancelled, CorrelationId = Run });

        foreach (var line in _out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var e = JsonSerializer.Deserialize(line, AGUIJsonSerializerContext.Default.BaseEvent);
            Assert.IsNotNull(e, line);
            Assert.AreEqual(JsonDocument.Parse(line).RootElement.GetProperty("type").GetString(), e.Type, line);
        }
    }

    [TestMethod]
    public void ToolCall_StartArgsEndThenResult_WithDurationAndFailure()
    {
        var started = new ToolCallInfo
        {
            ToolCallId = "c1", ToolCallName = "github__get_issue", Phase = ToolCallPhase.Started, ArgsSummary = "issue: 797",
        };
        _writer.Start();
        _writer.Report(Progress("Calling github__get_issue…", started));
        _writer.Report(Progress("done", started with { Phase = ToolCallPhase.Finished, Result = "open", DurationMs = 420 }));
        _writer.Report(Progress("failed", started with { ToolCallId = "c2", Phase = ToolCallPhase.Failed, Error = "404", DurationMs = 90 }));

        var lines = Lines().Skip(1).ToList();
        CollectionAssert.AreEqual(
            new[] { "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_RESULT", "TOOL_CALL_RESULT" },
            lines.Select(Type).ToArray());

        Assert.AreEqual("github__get_issue", Str(lines[0], "toolCallName"));
        Assert.IsTrue(lines.All(l => Str(l, "toolCallId") is "c1" or "c2"));

        // The args are still a JSON document, wrapping RockBot's readable summary.
        var args = JsonDocument.Parse(Str(lines[1], "delta")!).RootElement;
        Assert.AreEqual("issue: 797", args.GetProperty("summary").GetString());

        var ok = lines[3];
        Assert.AreEqual("open", Str(ok, "content"));
        var okMeta = ok.GetProperty("metadata").GetProperty("rockbot");
        Assert.AreEqual("finished", Str(okMeta, "status"));
        Assert.IsFalse(okMeta.GetProperty("failed").GetBoolean());
        Assert.AreEqual(420, okMeta.GetProperty("durationMs").GetInt64());

        var failed = lines[4];
        Assert.AreEqual("404", Str(failed, "content"));
        var failedMeta = failed.GetProperty("metadata").GetProperty("rockbot");
        Assert.AreEqual("failed", Str(failedMeta, "status"));
        Assert.IsTrue(failedMeta.GetProperty("failed").GetBoolean());

        // Each fact appears once: the result or error in content, the rest in metadata.
        foreach (var result in new[] { ok, failed })
        {
            Assert.IsFalse(result.TryGetProperty("rawEvent", out _));
            Assert.IsFalse(result.GetProperty("metadata").GetProperty("rockbot").TryGetProperty("error", out _));
        }
    }

    [TestMethod]
    public void ToolCall_WithoutArgs_HasNoArgsEvent()
    {
        _writer.Report(Progress("Calling now…", new ToolCallInfo { ToolCallId = "c1", ToolCallName = "now", Phase = ToolCallPhase.Started }));

        CollectionAssert.AreEqual(
            new[] { "RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_END" },
            Lines().Select(Type).ToArray());
    }

    [TestMethod]
    public void TextProgress_IsAnActivitySnapshot_PerSource()
    {
        _writer.Report(Progress("Reading <b>3</b> pages"));
        _writer.Report(Progress("Subagent working", source: ReplySource.Subagent("sub-1")));

        var lines = Lines().Skip(1).ToList();
        Assert.IsTrue(lines.All(l => Type(l) == "ACTIVITY_SNAPSHOT"));
        Assert.AreEqual(TurnEventMapper.ProgressActivityType, Str(lines[0], "activityType"));
        Assert.AreEqual("Reading 3 pages", lines[0].GetProperty("content").GetProperty("text").GetString());
        Assert.AreEqual("RockBot", lines[0].GetProperty("content").GetProperty("agentName").GetString());
        Assert.IsNull(Str(lines[0], "subagentRunId"));

        // A subagent's progress is attributed to it and kept apart from the turn's own activity.
        Assert.AreEqual("sub-1", Str(lines[1], "subagentRunId"));
        Assert.AreNotEqual(Str(lines[0], "messageId"), Str(lines[1], "messageId"));
    }

    [TestMethod]
    public void TaskList_SnapshotThenDeltas_ArePatchesAgainstTheState()
    {
        _writer.OnTaskListSnapshot(new TaskListSnapshot
        {
            ThreadId = Session, RunId = Run, Source = ReplySource.Primary, Items = [Item("1", "pending"), Item("2", "pending")],
        });
        _writer.OnTaskListDelta(new TaskListDelta { ThreadId = Session, Source = ReplySource.Primary, Item = Item("2", "completed") });
        _writer.OnTaskListDelta(new TaskListDelta { ThreadId = Session, Source = ReplySource.Primary, Item = Item("3", "pending") });
        _writer.OnTaskListSnapshot(new TaskListSnapshot { ThreadId = Session, Source = ReplySource.Subagent("a/b"), Items = [Item("1", "pending")] });
        _writer.OnTaskListDelta(new TaskListDelta { ThreadId = Session, Source = ReplySource.Subagent("a/b"), Item = Item("1", "completed") });

        var lines = Lines().Skip(1).ToList();

        var snapshot = lines[0].GetProperty("snapshot").GetProperty("taskLists").GetProperty("primary");
        Assert.AreEqual("STATE_SNAPSHOT", Type(lines[0]));
        Assert.AreEqual(2, snapshot.GetArrayLength());
        Assert.AreEqual("pending", snapshot[1].GetProperty("status").GetString());

        static JsonElement Op(JsonElement line) => line.GetProperty("delta")[0];

        Assert.AreEqual("STATE_DELTA", Type(lines[1]));
        Assert.AreEqual("replace", Str(Op(lines[1]), "op"));
        Assert.AreEqual("/taskLists/primary/1", Str(Op(lines[1]), "path"));
        Assert.AreEqual("completed", Op(lines[1]).GetProperty("value").GetProperty("status").GetString());

        Assert.AreEqual("add", Str(Op(lines[2]), "op"));
        Assert.AreEqual("/taskLists/primary/-", Str(Op(lines[2]), "path"));

        // A second list joins the state; its key is escaped as a JSON Pointer segment in patches.
        Assert.AreEqual("STATE_SNAPSHOT", Type(lines[3]));
        Assert.AreEqual(3, lines[3].GetProperty("snapshot").GetProperty("taskLists").GetProperty("primary").GetArrayLength());
        Assert.AreEqual("replace", Str(Op(lines[4]), "op"));
        Assert.AreEqual("/taskLists/a~1b/0", Str(Op(lines[4]), "path"));
    }

    [TestMethod]
    public void TaskList_DeltaForAListNeverShown_IsASnapshot()
    {
        // With no prior state event, the consumer has no /taskLists to patch: RFC 6902 'add'
        // needs the parent to exist, so the stream gives the whole state instead.
        _writer.OnTaskListDelta(new TaskListDelta { ThreadId = Session, Source = ReplySource.Primary, Item = Item("1", "in_progress") });
        _writer.OnTaskListDelta(new TaskListDelta { ThreadId = Session, Source = ReplySource.Worker("w1"), Item = Item("1", "pending") });
        _writer.OnTaskListDelta(new TaskListDelta { ThreadId = Session, Source = ReplySource.Primary, Item = Item("1", "completed") });

        var lines = Lines().Skip(1).ToList();
        CollectionAssert.AreEqual(
            new[] { "STATE_SNAPSHOT", "STATE_SNAPSHOT", "STATE_DELTA" },
            lines.Select(Type).ToArray());

        var first = lines[0].GetProperty("snapshot").GetProperty("taskLists");
        Assert.AreEqual("in_progress", first.GetProperty("primary")[0].GetProperty("status").GetString());

        // The worker's list joins the state without losing the turn's.
        var second = lines[1].GetProperty("snapshot").GetProperty("taskLists");
        Assert.IsTrue(second.TryGetProperty("primary", out _));
        Assert.AreEqual("pending", second.GetProperty("w1")[0].GetProperty("status").GetString());

        Assert.AreEqual("/taskLists/primary/0", Str(lines[2].GetProperty("delta")[0], "path"));
    }

    [TestMethod]
    public void Subagents_StartFinishAndError()
    {
        var at = DateTimeOffset.UtcNow;
        _writer.SubagentStarted(new SubagentStarted
        {
            SubagentRunId = "sub-1", Kind = ReplySourceKind.Subagent, Description = "research", ThreadId = Session,
            ParentToolCallId = "c9", StartedAt = at,
        });
        _writer.SubagentFinished(new SubagentFinished
        {
            SubagentRunId = "sub-1", Kind = ReplySourceKind.Subagent, ThreadId = Session, Outcome = SubagentOutcome.Success, FinishedAt = at,
        });
        _writer.SubagentFinished(new SubagentFinished
        {
            SubagentRunId = "sub-2", Kind = ReplySourceKind.Worker, ThreadId = Session, Outcome = SubagentOutcome.Timeout, FinishedAt = at,
        });

        var lines = Lines().Skip(1).ToList();
        CollectionAssert.AreEqual(
            new[] { "SUBAGENT_STARTED", "SUBAGENT_FINISHED", "SUBAGENT_ERROR" },
            lines.Select(Type).ToArray());
        Assert.AreEqual("research", Str(lines[0], "description"));
        Assert.AreEqual("c9", Str(lines[0], "parentToolCallId"));
        Assert.AreEqual(at.ToUnixTimeMilliseconds(), lines[0].GetProperty("timestamp").GetInt64());
        Assert.AreEqual("timeout", Str(lines[2], "code"));
        Assert.AreEqual("sub-2", Str(lines[2], "subagentRunId"));
    }

    [TestMethod]
    public void RunEvents_FromTheAgent_AreCustomEvents()
    {
        _writer.OnRunStarted(new RunStarted
        {
            ThreadId = Session, RunId = Run, AgentName = "RockBot", HeartbeatIntervalSeconds = 10, Timestamp = DateTimeOffset.UtcNow,
        });
        _writer.OnRunHeartbeat(new RunHeartbeat
        {
            ThreadId = Session, RunId = Run, Timestamp = DateTimeOffset.UtcNow, ElapsedSeconds = 12.5, Status = "Calling web_search",
        });

        var lines = Lines().Skip(1).ToList();
        Assert.AreEqual(TurnEventMapper.CustomNames.AgentRunStarted, Str(lines[0], "name"));
        Assert.AreEqual(10, lines[0].GetProperty("value").GetProperty("heartbeatIntervalSeconds").GetInt32());
        Assert.AreEqual(TurnEventMapper.CustomNames.Heartbeat, Str(lines[1], "name"));
        Assert.AreEqual("Calling web_search", lines[1].GetProperty("value").GetProperty("status").GetString());
    }

    [TestMethod]
    [DataRow(TurnResultOutcome.Error, "error")]
    [DataRow(TurnResultOutcome.TimedOut, "timeout")]
    [DataRow(TurnResultOutcome.AgentLost, "agent_lost")]
    public void FailedTurn_EndsWithRunError(TurnResultOutcome outcome, string code)
    {
        _writer.Finish(new TurnResult { Outcome = outcome, CorrelationId = Run, Error = "boom" });

        var last = Lines()[^1];
        Assert.AreEqual("RUN_ERROR", Type(last));
        Assert.AreEqual(code, Str(last, "code"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Str(last, "message")));
    }

    [TestMethod]
    public void ErrorTurn_WithAReply_WritesTheReplyBeforeRunError()
    {
        _writer.Finish(new TurnResult
        {
            Outcome = TurnResultOutcome.Error, CorrelationId = Run, Error = "LLM failed", Reply = Final("Sorry, that failed."),
        });

        CollectionAssert.AreEqual(
            new[] { "RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_ERROR" },
            Lines().Select(Type).ToArray());
        StringAssert.Contains(Str(Lines()[^1], "message"), "LLM failed");
    }

    [TestMethod]
    public void CancelledTurn_IsRunFinishedWithTheCancelledOutcome()
    {
        _writer.Finish(new TurnResult { Outcome = TurnResultOutcome.Cancelled, CorrelationId = Run });

        var last = Lines()[^1];
        Assert.AreEqual("RUN_FINISHED", Type(last));
        Assert.AreEqual("cancelled", last.GetProperty("outcome").GetProperty("type").GetString());
    }

    [TestMethod]
    public void Attachments_SentAndReturned_AreCustomEvents()
    {
        var sent = new AgentAttachment { Mime = "image/png", Path = "in.png" };
        var produced = new AgentAttachment { Mime = "image/png", Path = "chart.png", FileName = "Chart" };

        _writer.AttachmentSent(sent);
        _writer.Finish(new TurnResult { Outcome = TurnResultOutcome.Success, CorrelationId = Run, Reply = Final("Here", [produced]) });

        var lines = Lines();
        Assert.AreEqual(TurnEventMapper.CustomNames.AttachmentSent, Str(lines[1], "name"));
        Assert.AreEqual("in.png", lines[1].GetProperty("value").GetProperty("attachment").GetProperty("path").GetString());

        var attachments = lines.Single(l => Str(l, "name") == TurnEventMapper.CustomNames.Attachments);
        Assert.AreEqual(Str(lines.Single(l => Type(l) == "TEXT_MESSAGE_START"), "messageId"),
            attachments.GetProperty("value").GetProperty("messageId").GetString());
        Assert.AreEqual("chart.png", attachments.GetProperty("value").GetProperty("attachments")[0].GetProperty("path").GetString());
        Assert.AreEqual("RUN_FINISHED", Type(lines[^1]));
    }

    [TestMethod]
    public void Fail_BeforeAnythingElse_StillStartsTheRun()
    {
        _writer.Fail(TurnEventMapper.ErrorCodes.ClientError, "broker unreachable");

        var lines = Lines();
        CollectionAssert.AreEqual(new[] { "RUN_STARTED", "RUN_ERROR" }, lines.Select(Type).ToArray());
        Assert.AreEqual("client_error", Str(lines[1], "code"));
    }

    [TestMethod]
    public void NothingIsWritten_AfterTheTerminalEvent()
    {
        _writer.Start();
        _writer.Start();
        _writer.Finish(new TurnResult { Outcome = TurnResultOutcome.Success, CorrelationId = Run });
        _writer.Report(Progress("late"));
        _writer.SubagentStarted(new SubagentStarted
        {
            SubagentRunId = "s", Kind = ReplySourceKind.Subagent, Description = "d", ThreadId = Session, StartedAt = DateTimeOffset.UtcNow,
        });
        _writer.Fail("error", "late");

        Assert.IsTrue(_writer.IsClosed);
        CollectionAssert.AreEqual(new[] { "RUN_STARTED", "RUN_FINISHED" }, Lines().Select(Type).ToArray());
    }

    [TestMethod]
    public void ConcurrentWrites_NeverInterleaveWithinALine()
    {
        _writer.Start();
        Parallel.For(0, 400, i => _writer.Report(Progress($"line {i} " + new string('x', 200))));

        var lines = Lines();
        Assert.AreEqual(401, lines.Count);
        Assert.AreEqual(400, lines.Count(l => Type(l) == "ACTIVITY_SNAPSHOT"));
    }

    [TestMethod]
    public void Frontend_PassesOnlyThisSessionsBackgroundWork()
    {
        IUserFrontend frontend = new JsonLinesFrontend(_writer);
        var now = DateTimeOffset.UtcNow;

        frontend.OnSubagentStartedAsync(new SubagentStarted
        {
            SubagentRunId = "mine", Kind = ReplySourceKind.Subagent, Description = "d", ThreadId = Session, StartedAt = now,
        });
        frontend.OnSubagentStartedAsync(new SubagentStarted
        {
            SubagentRunId = "theirs", Kind = ReplySourceKind.Subagent, Description = "d", ThreadId = "blazor-session", StartedAt = now,
        });
        frontend.DisplayStatusAsync(Progress("subagent progress", source: ReplySource.Subagent("mine")));
        frontend.DisplayStatusAsync(Progress("other") with { SessionId = "blazor-session" });
        frontend.DisplayReplyAsync(Final("subagent result") with { Source = ReplySource.Subagent("mine"), Origin = null });
        frontend.DisplayReplyAsync(Final("other result") with { SessionId = "blazor-session" });
        // A primary task list reaching the frontend belongs to some other turn.
        frontend.OnTaskListSnapshotAsync(new TaskListSnapshot { ThreadId = Session, Source = ReplySource.Primary, Items = [] });
        frontend.OnTaskListSnapshotAsync(new TaskListSnapshot { ThreadId = Session, Source = ReplySource.Subagent("mine"), Items = [] });
        frontend.DisplayErrorAsync("not part of the run");

        var lines = Lines().Skip(1).ToList();
        CollectionAssert.AreEqual(
            new[]
            {
                "SUBAGENT_STARTED", "ACTIVITY_SNAPSHOT",
                "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "CUSTOM",
                "STATE_SNAPSHOT",
            },
            lines.Select(Type).ToArray());
        Assert.AreEqual("mine", Str(lines[0], "subagentRunId"));
        Assert.AreEqual("mine", Str(lines[2], "subagentRunId"));
        Assert.AreEqual(TurnEventMapper.CustomNames.Origin, Str(lines[5], "name"));
    }

    [TestMethod]
    public void Frontend_MatchesANamespacedSessionId()
    {
        // A subagent's tool progress carries the primary session as the agent namespaces it.
        IUserFrontend frontend = new JsonLinesFrontend(_writer);
        var call = new ToolCallInfo { ToolCallId = "c1", ToolCallName = "web_search", Phase = ToolCallPhase.Started };

        frontend.DisplayStatusAsync(Progress("Calling web_search…", call, ReplySource.Subagent("sub-1")) with
        {
            SessionId = $"session/{Session}",
        });
        frontend.DisplayStatusAsync(Progress("other") with { SessionId = "session/blazor-session" });

        var lines = Lines().Skip(1).ToList();
        CollectionAssert.AreEqual(new[] { "TOOL_CALL_START", "TOOL_CALL_END" }, lines.Select(Type).ToArray());
        Assert.AreEqual("sub-1", Str(lines[0], "subagentRunId"));
    }

    [TestMethod]
    [DataRow("cli-session", "cli-session")]
    [DataRow("session/cli-session", "cli-session")]
    [DataRow("Session/cli-session", "cli-session")]
    [DataRow("subagent-1", "subagent-1")]
    [DataRow(null, null)]
    public void RawSessionId_StripsTheNamespace(string? sessionId, string? expected)
    {
        Assert.AreEqual(expected, JsonLinesFrontend.RawSessionId(sessionId));
    }

    [TestMethod]
    public void Output_IsUtf8JsonWithReadableText()
    {
        _writer.Finish(new TurnResult { Outcome = TurnResultOutcome.Success, CorrelationId = Run, Reply = Final("→ it's \"done\" ✓") });

        StringAssert.Contains(_out.ToString(), "→ it's \\\"done\\\" ✓");
    }
}
