using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// The routing frontend without HTTP or a bus: which threads each <see cref="IUserFrontend"/>
/// callback reaches, and which AG-UI events it becomes.
/// </summary>
[TestClass]
public sealed class NotifyRoutingTests
{
    private const string Phone = "phone";
    private const string Laptop = "laptop";

    private NotifyHub _hub = null!;
    private AgUiUserFrontend _frontend = null!;
    private NotifySubscription _phone = null!;
    private NotifySubscription _laptop = null!;

    [TestInitialize]
    public void Init()
    {
        var options = new AgUiOptions();
        options.AllowedThreadIds.AddRange([Phone, Laptop]);
        _hub = new NotifyHub(options, epoch: "e1");
        _frontend = new AgUiUserFrontend(_hub, NullLogger<AgUiUserFrontend>.Instance);
        _phone = _hub.Subscribe(Phone, "e1-0")!;
        _laptop = _hub.Subscribe(Laptop, "e1-0")!;
    }

    [TestCleanup]
    public void Cleanup()
    {
        _phone.Dispose();
        _laptop.Dispose();
    }

    private static List<JsonElement> Drain(NotifySubscription subscription)
    {
        var events = new List<JsonElement>();
        while (subscription.Reader.TryRead(out var frame))
            events.Add(JsonDocument.Parse(frame.Json).RootElement);
        return events;
    }

    private static string[] Types(IEnumerable<JsonElement> events) => [.. events.Select(Sse.Type)];

    private static AgentReply Reply(string sessionId, string content = "Done.", bool isFinal = true) => new()
    {
        Content = content, SessionId = sessionId, AgentName = "RockBot", IsFinal = isFinal,
    };

    // ── Session matching (the rule shared with the CLI) ──────────────────────

    [TestMethod]
    [DataRow("phone", true, DisplayName = "the raw id")]
    [DataRow("session/phone", true, DisplayName = "the agent's working-memory form")]
    [DataRow("session/session/phone", false, DisplayName = "only one prefix is stripped")]
    [DataRow("Phone", false, DisplayName = "case matters")]
    [DataRow("phone2", false, DisplayName = "a longer id")]
    [DataRow("session/phone2", false, DisplayName = "a longer id, prefixed")]
    [DataRow("xsession/phone", false, DisplayName = "something before the prefix")]
    [DataRow("subagent/phone", false, DisplayName = "another namespace")]
    [DataRow("", false, DisplayName = "empty")]
    [DataRow(null, false, DisplayName = "null")]
    public void IsSessionsReply_AcceptsExactlyTheTwoFormsOfTheThread(string? sessionId, bool expected)
    {
        Assert.AreEqual(expected, SessionRouting.IsSessionsReply(Phone, sessionId));
    }

    [TestMethod]
    [DataRow("scheduled", true)]
    [DataRow("scheduled-system", true)]
    [DataRow("a2a-inbound", true)]
    [DataRow("a2a-inbound/ctx-1", true)]
    [DataRow("a2a-inbound-phone", false)]
    [DataRow("scheduled2", false)]
    [DataRow("patrol/daily", false)]
    [DataRow("blazor-session", false)]
    [DataRow(null, false)]
    public void IsBroadcastSession_IsTrueForTheSessionsThatBelongToNoConversation(string? sessionId, bool expected)
    {
        Assert.AreEqual(expected, SessionRouting.IsBroadcastSession(sessionId));
    }

    [TestMethod]
    public void TheSystemScheduledSession_IsTheAgentsConstant()
    {
        Assert.AreEqual(WellKnownSessions.ScheduledSystem, SessionRouting.ScheduledSystemSession);
    }

    // ── Which threads see what ───────────────────────────────────────────────

    [TestMethod]
    public async Task AReply_GoesToItsSessionsThread_AndNoOther()
    {
        await _frontend.DisplayReplyAsync(Reply(Phone));

        Assert.AreEqual(4, Drain(_phone).Count);
        Assert.AreEqual(0, Drain(_laptop).Count);
    }

    [TestMethod]
    public async Task AReplyInThePrefixedForm_GoesToTheSameThread()
    {
        await _frontend.DisplayStatusAsync(Reply("session/laptop", "Calling web_search…", isFinal: false));

        Assert.AreEqual(0, Drain(_phone).Count);
        Assert.AreEqual(1, Drain(_laptop).Count);
    }

    [TestMethod]
    [DataRow("blazor-session")]
    [DataRow("session/blazor-session")]
    [DataRow("session/session/phone")]
    [DataRow("phone-2")]
    public async Task AReplyForSomeOtherSession_GoesNowhere(string sessionId)
    {
        await _frontend.DisplayReplyAsync(Reply(sessionId));
        await _frontend.DisplayStatusAsync(Reply(sessionId, isFinal: false));

        Assert.AreEqual(0, Drain(_phone).Count);
        Assert.AreEqual(0, Drain(_laptop).Count);
    }

    [TestMethod]
    [DataRow("scheduled")]
    [DataRow("scheduled-system")]
    [DataRow("a2a-inbound")]
    public async Task AScheduledOrInboundA2AReply_GoesToEveryThread(string sessionId)
    {
        await _frontend.DisplayReplyAsync(Reply(sessionId, "Your morning summary."));

        foreach (var events in new[] { Drain(_phone), Drain(_laptop) })
        {
            CollectionAssert.AreEqual(
                new[] { "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "CUSTOM" }, Types(events));
            Assert.AreEqual("Your morning summary.", Sse.Str(events[1], "delta"));
        }
    }

    [TestMethod]
    public async Task WhatHasNoSession_GoesToEveryThread()
    {
        await _frontend.OnAgentNameChangedAsync("Rocky");
        await _frontend.OnMcpSignInStatusAsync(new McpSignInStatus { ServerName = "calendar", State = McpSignInState.Required });
        await _frontend.DisplayErrorAsync("Something broke.");

        foreach (var events in new[] { Drain(_phone), Drain(_laptop) })
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    TurnEventMapper.CustomNames.AgentName, TurnEventMapper.CustomNames.McpSignInStatus,
                    TurnEventMapper.CustomNames.Error,
                },
                events.Select(e => Sse.Str(e, "name")).ToArray());
            Assert.AreEqual("Rocky", events[0].GetProperty("value").GetProperty("agentName").GetString());
            Assert.AreEqual("calendar", events[1].GetProperty("value").GetProperty("serverName").GetString());
            Assert.AreEqual("required", events[1].GetProperty("value").GetProperty("state").GetString());
            Assert.AreEqual("Something broke.", events[2].GetProperty("value").GetProperty("message").GetString());
        }
    }

    [TestMethod]
    public async Task StructuredEvents_GoToTheThreadTheyName_Exactly()
    {
        var started = new SubagentStarted
        {
            SubagentRunId = "t1", Kind = ReplySourceKind.Subagent, Description = "Research", ThreadId = Phone,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await _frontend.OnSubagentStartedAsync(started);
        // These carry the raw id; the prefixed form would be some other producer's mistake.
        await _frontend.OnSubagentStartedAsync(started with { ThreadId = "session/phone" });
        await _frontend.OnSubagentStartedAsync(started with { ThreadId = "blazor-session" });

        CollectionAssert.AreEqual(new[] { "SUBAGENT_STARTED" }, Types(Drain(_phone)));
        Assert.AreEqual(0, Drain(_laptop).Count);
    }

    [TestMethod]
    public async Task AQuestionStatus_GoesToItsSessionsThread()
    {
        await _frontend.OnMcpQuestionStatusAsync(new McpQuestionStatus
        {
            SessionId = Laptop, QuestionId = "q1", State = McpQuestionState.Expired, Reason = "timed out",
        });

        Assert.AreEqual(0, Drain(_phone).Count);
        var e = Drain(_laptop).Single();
        Assert.AreEqual(TurnEventMapper.CustomNames.McpQuestionStatus, Sse.Str(e, "name"));
        Assert.AreEqual("q1", e.GetProperty("value").GetProperty("questionId").GetString());
        Assert.AreEqual("expired", e.GetProperty("value").GetProperty("state").GetString());
        Assert.AreEqual("timed out", e.GetProperty("value").GetProperty("reason").GetString());
    }

    // ── What each callback becomes ───────────────────────────────────────────

    [TestMethod]
    public async Task AnUnsolicitedReply_IsATextMessageWithItsOrigin()
    {
        var origin = new ReplyOrigin("blazor", "Compare the two vendors", DateTimeOffset.Parse("2026-11-14T08:00:00Z"), "blazor-session");
        await _frontend.DisplayReplyAsync(Reply(Phone, "Vendor A is cheaper.") with
        {
            Origin = origin, Source = ReplySource.Primary,
            Attachments = [new AgentAttachment { Mime = "image/png", Path = "chart.png" }],
        });

        var events = Drain(_phone);
        CollectionAssert.AreEqual(
            new[] { "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "CUSTOM", "CUSTOM" }, Types(events));
        var messageId = Sse.Str(events[0], "messageId");
        Assert.AreEqual("assistant", Sse.Str(events[0], "role"));
        Assert.AreEqual("Vendor A is cheaper.", Sse.Str(events[1], "delta"));
        Assert.AreEqual(TurnEventMapper.CustomNames.Attachments, Sse.Str(events[3], "name"));
        Assert.AreEqual(TurnEventMapper.CustomNames.Origin, Sse.Str(events[4], "name"));
        var value = events[4].GetProperty("value");
        Assert.AreEqual(messageId, value.GetProperty("messageId").GetString());
        Assert.AreEqual("blazor", value.GetProperty("origin").GetProperty("channel").GetString());
        Assert.AreEqual("Compare the two vendors", value.GetProperty("origin").GetProperty("promptSummary").GetString());
        Assert.AreEqual("primary", value.GetProperty("source").GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task AQuestionForm_IsAnMcpQuestionEvent_NotAMessage()
    {
        var form = new McpQuestionForm
        {
            QuestionId = "q1", ServerName = "scheduler", ToolName = "create_event", Message = "Which time zone?",
            ExpiresAt = DateTimeOffset.Parse("2026-11-14T10:00:00Z"),
            Fields = [new McpQuestionField { Name = "tz", Kind = McpQuestionFieldKind.String, Title = "Time zone", Required = true }],
        };
        await _frontend.DisplayReplyAsync(Reply(Phone, "The scheduler asks: Which time zone?") with
        {
            ContentType = McpQuestionForm.ContentType,
            StructuredData = JsonSerializer.Serialize(form, TurnEventMapper.PayloadJson),
            Source = ReplySource.Bridge,
        });

        var e = Drain(_phone).Single();
        Assert.AreEqual("CUSTOM", Sse.Type(e));
        Assert.AreEqual(TurnEventMapper.CustomNames.McpQuestion, Sse.Str(e, "name"));
        var value = e.GetProperty("value");
        Assert.AreEqual(Phone, value.GetProperty("sessionId").GetString());
        Assert.AreEqual("The scheduler asks: Which time zone?", value.GetProperty("text").GetString());
        Assert.AreEqual("q1", value.GetProperty("form").GetProperty("questionId").GetString());
        Assert.AreEqual("tz", value.GetProperty("form").GetProperty("fields")[0].GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task AQuestionFormThatIsNotJson_StillArrives_WithItsText()
    {
        await _frontend.DisplayReplyAsync(Reply(Phone, "Which time zone?") with
        {
            ContentType = McpQuestionForm.ContentType, StructuredData = "{not json",
        });

        var value = Drain(_phone).Single().GetProperty("value");
        Assert.AreEqual("Which time zone?", value.GetProperty("text").GetString());
        Assert.IsFalse(value.TryGetProperty("form", out _));
    }

    [TestMethod]
    public async Task Progress_IsOneActivityPerSource()
    {
        var subagent = Reply("session/phone", "Reading the report", isFinal: false) with
        {
            AgentName = "subagent-t1", Source = ReplySource.Subagent("t1"),
        };
        await _frontend.DisplayStatusAsync(subagent);
        await _frontend.DisplayStatusAsync(subagent with { Content = "Summarising" });
        await _frontend.DisplayStatusAsync(Reply(Phone, "Working", isFinal: false) with { AgentName = "ResearchAgent", Source = ReplySource.A2A });
        await _frontend.DisplayStatusAsync(Reply(Phone, "Still at it", isFinal: false));

        var events = Drain(_phone);
        CollectionAssert.AreEqual(
            new[] { "ACTIVITY_SNAPSHOT", "ACTIVITY_SNAPSHOT", "ACTIVITY_SNAPSHOT", "ACTIVITY_SNAPSHOT" }, Types(events));
        CollectionAssert.AreEqual(
            new[] { "notify-progress-t1", "notify-progress-t1", "notify-progress-ResearchAgent", "notify-progress-RockBot" },
            events.Select(e => Sse.Str(e, "messageId")).ToArray());
        Assert.AreEqual("t1", Sse.Str(events[0], "subagentRunId"));
        Assert.AreEqual("Summarising", events[1].GetProperty("content").GetProperty("text").GetString());
        Assert.AreEqual(TurnEventMapper.ProgressActivityType, Sse.Str(events[0], "activityType"));
    }

    [TestMethod]
    public async Task AToolCallInProgress_IsToolCallEvents()
    {
        await _frontend.DisplayStatusAsync(Reply("session/phone", "Calling web_search…", isFinal: false) with
        {
            AgentName = "subagent-t1", Source = ReplySource.Subagent("t1"),
            ToolCall = new ToolCallInfo { ToolCallId = "c1", ToolCallName = "web_search", Phase = ToolCallPhase.Started },
        });

        var events = Drain(_phone);
        CollectionAssert.AreEqual(new[] { "TOOL_CALL_START", "TOOL_CALL_END" }, Types(events));
        Assert.AreEqual("t1", Sse.Str(events[0], "subagentRunId"));
    }

    [TestMethod]
    public async Task ACompletion_ClosesTheSourcesActivity_AndIsThenDeliveredAsItsResult()
    {
        var progress = Reply(Phone, "Reading", isFinal: false) with { AgentName = "subagent-t1", Source = ReplySource.Subagent("t1") };
        await _frontend.DisplayStatusAsync(progress);
        await _frontend.DisplayStatusAsync(progress with { Content = "The report says revenue is up.", IsCompletion = true });

        var events = Drain(_phone);
        CollectionAssert.AreEqual(
            new[] { "ACTIVITY_SNAPSHOT", "ACTIVITY_SNAPSHOT", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "CUSTOM" },
            Types(events));
        // The closing snapshot is the same activity the progress went to.
        Assert.AreEqual(Sse.Str(events[0], "messageId"), Sse.Str(events[1], "messageId"));
        Assert.IsTrue(events[1].GetProperty("content").GetProperty("done").GetBoolean());
        Assert.IsFalse(events[0].GetProperty("content").TryGetProperty("done", out _));
        // The result is the subagent's, and says so.
        Assert.AreEqual("t1", Sse.Str(events[2], "subagentRunId"));
        Assert.AreEqual("The report says revenue is up.", Sse.Str(events[3], "delta"));
        Assert.AreEqual(TurnEventMapper.CustomNames.Origin, Sse.Str(events[5], "name"));
        Assert.AreEqual("subagent", events[5].GetProperty("value").GetProperty("source").GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task SubagentLifecycle_IsSubagentEvents()
    {
        await _frontend.OnSubagentStartedAsync(new SubagentStarted
        {
            SubagentRunId = "t1", Kind = ReplySourceKind.Subagent, Description = "Research", ThreadId = Phone,
            ParentToolCallId = "c9", StartedAt = DateTimeOffset.UtcNow,
        });
        await _frontend.OnSubagentFinishedAsync(new SubagentFinished
        {
            SubagentRunId = "t1", Kind = ReplySourceKind.Subagent, ThreadId = Phone, Outcome = SubagentOutcome.Success,
            FinishedAt = DateTimeOffset.UtcNow,
        });
        await _frontend.OnSubagentFinishedAsync(new SubagentFinished
        {
            SubagentRunId = "t2", Kind = ReplySourceKind.Worker, ThreadId = Phone, Outcome = SubagentOutcome.Failed,
            Error = "out of steps", FinishedAt = DateTimeOffset.UtcNow,
        });

        var events = Drain(_phone);
        CollectionAssert.AreEqual(new[] { "SUBAGENT_STARTED", "SUBAGENT_FINISHED", "SUBAGENT_ERROR" }, Types(events));
        Assert.AreEqual("t1", Sse.Str(events[0], "subagentRunId"));
        Assert.AreEqual("c9", Sse.Str(events[0], "parentToolCallId"));
        Assert.AreEqual("out of steps", Sse.Str(events[2], "message"));
    }

    [TestMethod]
    public async Task TaskLists_AreAlwaysWholeSnapshots_AndAFinishedSubagentsListIsDropped()
    {
        var source = ReplySource.Subagent("t1");
        await _frontend.OnTaskListSnapshotAsync(new TaskListSnapshot
        {
            ThreadId = Phone, Source = source,
            Items = [new TaskListItem { Id = "1", Description = "search", Status = "in_progress" }],
        });
        await _frontend.OnTaskListDeltaAsync(new TaskListDelta
        {
            ThreadId = Phone, Source = source, Item = new TaskListItem { Id = "1", Description = "search", Status = "completed" },
        });
        await _frontend.OnTaskListDeltaAsync(new TaskListDelta
        {
            ThreadId = Phone, Source = source, Item = new TaskListItem { Id = "2", Description = "write", Status = "pending" },
        });
        await _frontend.OnSubagentFinishedAsync(new SubagentFinished
        {
            SubagentRunId = "t1", Kind = ReplySourceKind.Subagent, ThreadId = Phone, Outcome = SubagentOutcome.Success,
            FinishedAt = DateTimeOffset.UtcNow,
        });

        var events = Drain(_phone);
        // A client can join the stream at any event, so there is never a patch to apply.
        CollectionAssert.AreEqual(
            new[] { "STATE_SNAPSHOT", "STATE_SNAPSHOT", "STATE_SNAPSHOT", "SUBAGENT_FINISHED", "STATE_SNAPSHOT" }, Types(events));
        var list = events[2].GetProperty("snapshot").GetProperty("taskLists").GetProperty("t1");
        Assert.AreEqual(2, list.GetArrayLength());
        Assert.AreEqual("completed", list[0].GetProperty("status").GetString());
        Assert.AreEqual("pending", list[1].GetProperty("status").GetString());
        Assert.IsFalse(events[4].GetProperty("snapshot").GetProperty("taskLists").TryGetProperty("t1", out _));
    }

    [TestMethod]
    public async Task LateRunEvents_SayWhichRunTheyBelongTo()
    {
        var at = DateTimeOffset.Parse("2026-11-14T09:00:00Z");
        await _frontend.OnRunStartedAsync(new RunStarted
        {
            ThreadId = Phone, RunId = "run-1", AgentName = "RockBot", HeartbeatIntervalSeconds = 15, Timestamp = at,
        });
        await _frontend.OnRunHeartbeatAsync(new RunHeartbeat
        {
            ThreadId = Phone, RunId = "run-1", Timestamp = at, ElapsedSeconds = 240, Status = "Calling web_search…",
        });
        await _frontend.OnRunFinishedAsync(new RunFinished
        {
            ThreadId = Phone, RunId = "run-1", Outcome = RunOutcome.Error, Error = "the model refused", Timestamp = at,
        });

        var events = Drain(_phone);
        CollectionAssert.AreEqual(
            new[]
            {
                TurnEventMapper.CustomNames.AgentRunStarted, TurnEventMapper.CustomNames.Heartbeat,
                TurnEventMapper.CustomNames.RunFinished,
            },
            events.Select(e => Sse.Str(e, "name")).ToArray());
        Assert.IsTrue(events.All(e => e.GetProperty("value").GetProperty("runId").GetString() == "run-1"));
        Assert.AreEqual("Calling web_search…", events[1].GetProperty("value").GetProperty("status").GetString());
        Assert.AreEqual("error", events[2].GetProperty("value").GetProperty("outcome").GetString());
        Assert.AreEqual("the model refused", events[2].GetProperty("value").GetProperty("error").GetString());
        Assert.AreEqual(0, Drain(_laptop).Count);
    }

    [TestMethod]
    public async Task AnActiveWorkSnapshot_GivesEachThreadItsOwnWork()
    {
        await _frontend.OnActiveWorkSnapshotAsync(new ActiveStatusResponse
        {
            Subagents = [], IsProcessing = false,
            Work =
            [
                new ActiveWorkItem { TaskId = "t1", Kind = ReplySourceKind.Subagent, Description = "Research", ThreadId = Phone, StartedAt = DateTimeOffset.UtcNow },
                new ActiveWorkItem { TaskId = "t2", Kind = ReplySourceKind.Worker, Description = "Other", ThreadId = "blazor-session", StartedAt = DateTimeOffset.UtcNow },
            ],
        });

        var phone = Drain(_phone).Single();
        Assert.AreEqual(TurnEventMapper.CustomNames.ActiveWork, Sse.Str(phone, "name"));
        Assert.AreEqual("t1", phone.GetProperty("value").GetProperty("work").EnumerateArray().Single().GetProperty("taskId").GetString());
        Assert.AreEqual(0, Drain(_laptop).Single().GetProperty("value").GetProperty("work").GetArrayLength());
    }
}
