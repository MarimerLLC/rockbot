using System.Text.Json;
using AGUI.Abstractions;
using RockBot.UserProxy;
using RockBot.UserProxy.AgUi;
using RockBot.UserProxy.ChatState;

namespace RockBot.AgUi.Client.Tests;

/// <summary>
/// The AG-UI-event to chat-event mapping, over recorded streams: the chat events each produces,
/// and the chat a view ends up with when <see cref="ChatReducer"/> folds them.
/// </summary>
[TestClass]
public sealed class AgUiEventMapperTests
{
    private const string Thread = Recorded.Thread;

    private static async Task<List<ChatEvent>> MapAsync(AgUiEventMapper mapper, string sse)
    {
        var mapped = new List<ChatEvent>();
        foreach (var e in await Recorded.EventsAsync(sse))
            mapped.AddRange(mapper.Map(e));
        return mapped;
    }

    /// <summary>The chat after a turn: what the client applies when it sends, then the run's own events.</summary>
    private static async Task<(List<ChatEvent> Events, ChatViewState State)> RunAsync(
        string runId, string text, string sse, ChatViewState? from = null)
    {
        var mapper = AgUiEventMapper.ForRun(Thread, runId);
        var events = await MapAsync(mapper, sse);
        events.AddRange(mapper.StreamEnded());
        var state = ChatReducer.Replay([.. AgUiEventMapper.BeginTurn(Thread, runId, text, "me"), .. events], from);
        return (events, state);
    }

    // ── A run ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Run_WithAToolCallAndATaskList_MapsToTheTurnsChatEvents()
    {
        var (events, _) = await RunAsync("run-1", "Hello", Recorded.RunWithToolCallAndTaskList);

        CollectionAssert.AreEqual(
            new[]
            {
                typeof(TurnHeartbeat),            // rockbot.agent_run_started
                typeof(TurnProgress),             // ACTIVITY_SNAPSHOT
                typeof(TaskListSnapshotReceived), // STATE_SNAPSHOT
                typeof(ToolCallReported),         // TOOL_CALL_START
                typeof(ToolCallReported),         // TOOL_CALL_ARGS: the same call, with its arguments
                typeof(ToolCallReported),         // TOOL_CALL_RESULT
                typeof(TaskListDeltaReceived),    // STATE_DELTA replace
                typeof(TaskListDeltaReceived),    // STATE_DELTA add
                typeof(TurnEnded),                // the reply, its attachments and RUN_FINISHED, as one event
            },
            events.Select(e => e.GetType()).ToArray());

        var progress = events.OfType<TurnProgress>().Single();
        Assert.AreEqual("run-1", progress.TurnId);
        Assert.AreEqual("Thinking…", progress.Content);

        var snapshot = events.OfType<TaskListSnapshotReceived>().Single().Snapshot;
        Assert.AreEqual(ReplySourceKind.Primary, snapshot.Source.Kind);
        Assert.AreEqual("run-1", snapshot.RunId, "the chat state only shows a primary task list that names its run");
        Assert.AreEqual(2, snapshot.Items.Count);

        var calls = events.OfType<ToolCallReported>().ToList();
        Assert.IsTrue(calls.All(c => c is { Category: MessageCategory.PrimaryProgress, AgentName: null }));
        Assert.IsTrue(calls.All(c => c.ToolCall is { ToolCallId: "call-633ad0ef", ToolCallName: "web_search" }));
        Assert.AreEqual(ToolCallPhase.Started, calls[0].ToolCall.Phase);
        Assert.AreEqual("query: Hello", calls[1].ToolCall.ArgsSummary);
        Assert.AreEqual(ToolCallPhase.Finished, calls[2].ToolCall.Phase);
        Assert.AreEqual("3 results", calls[2].ToolCall.Result);
        Assert.AreEqual(150, calls[2].ToolCall.DurationMs);

        var deltas = events.OfType<TaskListDeltaReceived>().Select(d => d.Delta).ToList();
        Assert.AreEqual(("1", "completed"), (deltas[0].Item.Id, deltas[0].Item.Status));
        Assert.AreEqual(("3", "pending"), (deltas[1].Item.Id, deltas[1].Item.Status));
        Assert.IsTrue(deltas.All(d => d.RunId == "run-1"));

        var ended = events.OfType<TurnEnded>().Single();
        Assert.AreEqual(TurnOutcome.Completed, ended.Outcome);
        Assert.AreEqual("run-1", ended.TurnId);
        Assert.AreEqual("2f3dead3", ended.MessageId, "the chat message keeps the AG-UI message id, which feedback would name");
        Assert.AreEqual("Echo: Hello", ended.Reply!.Content);
        Assert.AreEqual("RockBot", ended.Reply.AgentName);
        Assert.AreEqual("charts/q3 sales.png", ended.Reply.Attachments!.Single().Path);

        // Events carry the time the proxy stamped, so a recording always maps the same way.
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1794689046329).UtcDateTime, ended.Timestamp);
    }

    [TestMethod]
    public async Task Run_WithAToolCallAndATaskList_ReducesToTheChatAViewShows()
    {
        var (_, state) = await RunAsync("run-1", "Hello", Recorded.RunWithToolCallAndTaskList);

        Assert.IsNull(state.CurrentTurn, "the run ended, so the chat is no longer busy");
        Assert.IsFalse(state.HasOpenActivityLog);

        CollectionAssert.AreEqual(
            new[] { MessageCategory.UserInput, MessageCategory.PrimaryProgress, MessageCategory.PrimaryFinal },
            state.Messages.Select(m => m.Category).ToArray());

        var log = state.Messages[1];
        Assert.IsTrue(log.IsActivityLog);
        Assert.AreEqual(2, log.ActivityLogEntries.Count, "the progress line, and one entry for the tool call's three reports");
        var call = log.ActivityLogEntries[1].ToolCall!;
        Assert.AreEqual(ToolCallPhase.Finished, call.Phase);
        Assert.AreEqual("query: Hello", call.ArgsSummary, "the arguments from the call's start are kept on its result");
        Assert.AreEqual("web_search finished (150 ms)", log.ActivityLogEntries[1].Content);

        var plan = state.TaskLists[ReplySourceKind.Primary];
        CollectionAssert.AreEqual(
            new[] { "1:completed", "2:pending", "3:pending" },
            plan.Items.Select(i => $"{i.Id}:{i.Status}").ToArray());

        var reply = state.Messages[2];
        Assert.AreEqual("Echo: Hello", reply.Content);
        Assert.AreEqual("image/png", reply.Attachments!.Single().Mime);
    }

    [TestMethod]
    public async Task Run_RemembersTheUrlOfEachAttachment()
    {
        var mapper = AgUiEventMapper.ForRun(Thread, "run-1");
        await MapAsync(mapper, Recorded.RunWithToolCallAndTaskList);

        Assert.AreEqual("/attachments/charts/q3%20sales.png", mapper.AttachmentUrls["charts/q3 sales.png"]);
    }

    [TestMethod]
    public async Task Run_ThatTheProxyStoppedWaitingFor_EndsAsTimedOut_AndTheLateReplyArrivesOnTheNotifyStream()
    {
        var mapper = AgUiEventMapper.ForRun(Thread, "run-silent");
        var run = await MapAsync(mapper, Recorded.RunThatTimedOut);

        var ended = (TurnEnded)run.Single();
        Assert.AreEqual(TurnOutcome.TimedOut, ended.Outcome);
        Assert.IsTrue(mapper.ReplyPending);

        var state = ChatReducer.Replay([.. AgUiEventMapper.BeginTurn(Thread, "run-silent", "silent", "me"), .. run]);
        Assert.IsNull(state.CurrentTurn, "the user can type again");
        Assert.AreEqual("No reply received (timeout)", state.Messages[^1].Content);

        // The session tells the notify mapper which run that was; the stream's task list for it
        // is keyed "primary" and doesn't say.
        var notify = AgUiEventMapper.ForNotify(Thread);
        notify.LateRunId = "run-silent";
        var late = await MapAsync(notify, Recorded.NotifyLateReply);

        CollectionAssert.AreEqual(
            new[]
            {
                typeof(TurnHeartbeat), typeof(TaskListSnapshotReceived), typeof(TurnHeartbeat),
                typeof(ReplyReceived), typeof(LateRunFinished),
            },
            late.Select(e => e.GetType()).ToArray());
        Assert.AreEqual("run-silent", late.OfType<TaskListSnapshotReceived>().Single().Snapshot.RunId);

        var working = ChatReducer.Replay(late.Take(3), state);
        Assert.AreEqual("Reading the report", working.ThinkingMessage, "the agent is still at it, and says what it is doing");
        Assert.AreEqual(1, working.TaskLists[ReplySourceKind.Primary].Items.Count);

        var done = ChatReducer.Replay(late.Skip(3), working);
        Assert.IsNull(done.ThinkingMessage);
        var reply = done.Messages[^1];
        Assert.AreEqual(MessageCategory.PrimaryFinal, reply.Category);
        Assert.AreEqual("Sorry for the wait. Here it is.", reply.Content);
        Assert.AreEqual("late-1", reply.MessageId);
    }

    [TestMethod]
    [DataRow("agent_lost", TurnOutcome.AgentLost, "Lost contact with the agent: gone")]
    [DataRow("error", TurnOutcome.AgentError, "Error: gone")]
    [DataRow("proxy_stopping", TurnOutcome.Failed, "Error: gone")]
    [DataRow("interrupts_pending", TurnOutcome.Failed, "Error: gone")]
    public void RunError_EndsTheTurn_WithTheOutcomeItsCodeMeans(string code, TurnOutcome outcome, string shown)
    {
        var mapper = AgUiEventMapper.ForRun(Thread, "run-1");
        var ended = (TurnEnded)mapper.Map(new RunErrorEvent { Code = code, Message = "gone" }).Single();

        Assert.AreEqual(outcome, ended.Outcome);
        var state = ChatReducer.Replay([.. AgUiEventMapper.BeginTurn(Thread, "run-1", "Hi", "me"), ended]);
        Assert.IsNull(state.CurrentTurn);
        Assert.AreEqual(shown, state.Messages[^1].Content);
        Assert.IsTrue(state.Messages[^1].IsError);
    }

    [TestMethod]
    public void RunFinished_Cancelled_EndsTheTurnAsCancelled()
    {
        var mapper = AgUiEventMapper.ForRun(Thread, "run-1");
        var ended = (TurnEnded)mapper.Map(new RunFinishedEvent
        {
            ThreadId = Thread, RunId = "run-1", Outcome = new RunFinishedCancelledOutcome(),
        }).Single();

        Assert.AreEqual(TurnOutcome.Cancelled, ended.Outcome);
    }

    [TestMethod]
    public void ARunWhoseStreamEndsWithoutATerminalEvent_FailsTheTurn_SoTheChatIsNotLeftBusy()
    {
        var mapper = AgUiEventMapper.ForRun(Thread, "run-1");
        mapper.Map(new RunStartedEvent { ThreadId = Thread, RunId = "run-1" });

        var ended = (TurnEnded)mapper.StreamEnded().Single();

        Assert.AreEqual(TurnOutcome.Failed, ended.Outcome);
        var state = ChatReducer.Replay([.. AgUiEventMapper.BeginTurn(Thread, "run-1", "Hi", "me"), ended]);
        Assert.IsNull(state.CurrentTurn);
        Assert.AreEqual(0, mapper.StreamEnded().Count, "only once");
    }

    [TestMethod]
    public async Task ARunThatEnded_MapsNothingMore()
    {
        var mapper = AgUiEventMapper.ForRun(Thread, "run-1");
        await MapAsync(mapper, Recorded.RunWithToolCallAndTaskList);

        Assert.IsTrue(mapper.Ended);
        Assert.AreEqual(0, mapper.StreamEnded().Count);
        Assert.AreEqual(0, mapper.Map(new RunErrorEvent { Code = "error", Message = "late" }).Count);
    }

    // ── The notify stream ────────────────────────────────────────────────────

    [TestMethod]
    public async Task Notify_ASubagentsLifeAndAScheduledReport_MapToChatEvents()
    {
        var mapper = AgUiEventMapper.ForNotify(Thread);
        mapper.PrimaryAgentName = "RockBot";
        var events = await MapAsync(mapper, Recorded.NotifySubagentAndScheduledReply);

        CollectionAssert.AreEqual(
            new[]
            {
                typeof(ActivityReported),         // the subagent's progress
                typeof(SubagentRunStarted),
                typeof(TaskListSnapshotReceived), // its task list
                typeof(ActivityReported),         // content.done: its log closes
                typeof(ReplyReceived),            // its result
                typeof(SubagentRunFinished),
                typeof(ReplyReceived),            // the scheduled task's report
            },
            events.Select(e => e.GetType()).ToArray());

        var progress = (ActivityReported)events[0];
        Assert.AreEqual(MessageCategory.SubagentActivity, progress.Category);
        Assert.AreEqual("subagent-t36d90f", progress.AgentName);
        Assert.AreEqual("Reading the price list", progress.Content);
        Assert.IsFalse(progress.IsCompletion);

        var started = ((SubagentRunStarted)events[1]).Started;
        Assert.AreEqual(("t36d90f", "subagent", "Research the vendors"), (started.SubagentRunId, started.Kind, started.Description));

        var list = ((TaskListSnapshotReceived)events[2]).Snapshot;
        Assert.AreEqual((ReplySourceKind.Subagent, "t36d90f"), (list.Source.Kind, list.Source.TaskId));

        Assert.IsTrue(((ActivityReported)events[3]).IsCompletion);

        var result = (ReplyReceived)events[4];
        Assert.AreEqual(MessageCategory.SubagentActivity, result.Category);
        Assert.AreEqual("subagent-t36d90f", result.Reply.AgentName);
        Assert.AreEqual("Vendor B is the cheapest by 12%.", result.Reply.Content,
            "work that started on this thread through this channel gets no origin line");
        Assert.AreEqual("Hello notify", result.Reply.Origin!.PromptSummary);

        Assert.AreEqual(SubagentOutcome.Success, ((SubagentRunFinished)events[5]).Finished.Outcome);

        var report = (ReplyReceived)events[6];
        Assert.AreEqual(MessageCategory.ScheduledUser, report.Category);
        Assert.AreEqual("scheduled", report.Reply.SessionId);
        Assert.AreEqual("scheduler", report.Reply.Origin!.Channel);
        StringAssert.StartsWith(report.Reply.Content, "> ↳ Re: \"Daily summary\"\n> started ");
        StringAssert.Contains(report.Reply.Content, " from scheduler · ");
        StringAssert.EndsWith(report.Reply.Content, "\n\n**Daily summary.** Nothing needs your attention today.");
    }

    [TestMethod]
    public async Task Notify_ASubagentsLifeAndAScheduledReport_ReduceToClosedLogsAndTwoMessages()
    {
        var mapper = AgUiEventMapper.ForNotify(Thread);
        mapper.PrimaryAgentName = "RockBot";
        var events = await MapAsync(mapper, Recorded.NotifySubagentAndScheduledReply);

        // While the subagent works: an open log (the header's indicator) and its plan.
        var working = ChatReducer.Replay(events.Take(3));
        var indicator = working.ActiveStatusIndicators.Single();
        Assert.AreEqual("subagent-t36d90f", indicator.AgentName);
        Assert.AreEqual("Compare prices", working.TaskLists["t36d90f"].CurrentItem!.Description);

        var state = ChatReducer.Replay(events);
        Assert.AreEqual(0, state.ActiveStatusIndicators.Count, "nothing is left spinning");
        Assert.IsFalse(state.HasOpenActivityLog);
        Assert.AreEqual(0, state.TaskLists.Count, "the finished subagent's plan goes with it");

        CollectionAssert.AreEqual(
            new[] { MessageCategory.SubagentActivity, MessageCategory.SubagentActivity, MessageCategory.ScheduledUser },
            state.Messages.Select(m => m.Category).ToArray());
        Assert.IsTrue(state.Messages[0].IsActivityLog);
        Assert.AreEqual(1, state.Messages[0].ActivityLogEntries.Count(e => e.Content == "Reading the price list"));
        Assert.AreEqual("Vendor B is the cheapest by 12%.", state.Messages[1].Content);
        Assert.AreEqual("cd433eaf", state.Messages[1].MessageId);
        Assert.IsTrue(state.Messages[2].IsExpanded, "the user's own scheduled task is shown open");
    }

    [TestMethod]
    [DataRow("scheduled", "scheduled", MessageCategory.ScheduledUser)]
    [DataRow("scheduled", "scheduled-system", MessageCategory.ScheduledSystem)]
    [DataRow("a2a", "a2a-inbound/ctx-1", MessageCategory.A2AActivity)]
    [DataRow("a2a", "agui-session", MessageCategory.PrimaryFinal)]
    [DataRow("primary", "agui-session", MessageCategory.PrimaryFinal)]
    [DataRow("bridge", "agui-session", MessageCategory.PrimaryFinal)]
    public void Notify_AReplysCategory_ComesFromItsSourceAndItsSession(string kind, string session, MessageCategory expected)
    {
        var mapper = AgUiEventMapper.ForNotify(Thread);
        var events = new List<ChatEvent>();
        var origin = JsonSerializer.Serialize(new { messageId = "m1", sessionId = session, source = new { kind } });
        foreach (var e in Message("m1", "Hello", origin))
            events.AddRange(mapper.Map(e));

        var reply = (ReplyReceived)events.Single();
        Assert.AreEqual(expected, reply.Category);
        Assert.AreEqual(session, reply.Reply.SessionId);
    }

    [TestMethod]
    public void Notify_AMessageWithoutItsOrigin_IsStillDelivered_WhenTheNextEventArrives()
    {
        var mapper = AgUiEventMapper.ForNotify(Thread);
        var events = new List<ChatEvent>();
        foreach (var e in Message("m1", "Hello", origin: null))
            events.AddRange(mapper.Map(e));
        Assert.AreEqual(0, events.Count, "held, in case attachments or an origin follow");

        events.AddRange(mapper.Map(Custom(RockBotEvents.Error, """{"message":"The bus dropped."}""")));

        Assert.AreEqual("Hello", ((ReplyReceived)events[0]).Reply.Content);
        Assert.AreEqual("The bus dropped.", ((ErrorReported)events[1]).Message);
    }

    [TestMethod]
    public void Notify_AnotherAgentsProgress_IsA2AActivity_AndThePrimaryAgentsIsItsOwn()
    {
        var mapper = AgUiEventMapper.ForNotify(Thread);
        mapper.PrimaryAgentName = "RockBot";

        var remote = (ActivityReported)mapper.Map(Activity("notify-progress-ResearchAgent", "ResearchAgent", "Searching")).Single();
        Assert.AreEqual((MessageCategory.A2AActivity, "ResearchAgent"), (remote.Category, remote.AgentName));

        // A run this client gave up on: the agent's own progress, in the one primary log.
        var own = (ActivityReported)mapper.Map(Activity("notify-progress-RockBot", "RockBot", "Still reading")).Single();
        Assert.AreEqual((MessageCategory.PrimaryProgress, null), (own.Category, own.AgentName));
    }

    [TestMethod]
    public void Notify_AWorkersProgress_GoesToTheLogItsLifecycleEventsNamed()
    {
        var mapper = AgUiEventMapper.ForNotify(Thread);
        var started = (SubagentRunStarted)mapper.Map(new SubagentStartedEvent
        {
            SubagentRunId = "w1", Name = "worker", Description = "Fetch the page",
        }).Single();
        Assert.AreEqual(ReplySourceKind.Worker, started.Started.Kind, "without rawEvent the kind is the event's name");

        var call = (ToolCallReported)mapper.Map(new ToolCallStartEvent
        {
            ToolCallId = "c9", ToolCallName = "fetch", SubagentRunId = "w1",
        }).Single();
        Assert.AreEqual((MessageCategory.SubagentActivity, "worker-w1"), (call.Category, call.AgentName));

        var failed = (SubagentRunFinished)mapper.Map(new SubagentErrorEvent
        {
            SubagentRunId = "w1", Code = "timeout", Message = "took too long",
        }).Single();
        Assert.AreEqual(("worker", "timeout", "took too long"), (failed.Finished.Kind, failed.Finished.Outcome, failed.Finished.Error));
    }

    [TestMethod]
    public void Notify_AFailedToolCall_CarriesItsError()
    {
        var mapper = AgUiEventMapper.ForNotify(Thread);
        mapper.Map(new ToolCallStartEvent { ToolCallId = "c1", ToolCallName = "fetch", SubagentRunId = "t1" });

        var failed = (ToolCallReported)mapper.Map(new ToolCallResultEvent
        {
            MessageId = "r1", ToolCallId = "c1", Content = "404", SubagentRunId = "t1",
            Metadata = JsonSerializer.SerializeToElement(new { rockbot = new { failed = true, durationMs = 20 } }),
        }).Single();

        Assert.AreEqual(ToolCallPhase.Failed, failed.ToolCall.Phase);
        Assert.AreEqual("404", failed.ToolCall.Error);
        Assert.AreEqual("fetch failed: 404", failed.Content);
    }

    [TestMethod]
    public void Notify_TheStreamsOwnAndUncoveredEvents_MapToNothing()
    {
        var mapper = AgUiEventMapper.ForNotify(Thread);

        Assert.AreEqual(0, mapper.Map(Custom(RockBotEvents.Connected, """{"threadId":"agui-session"}""")).Count);
        Assert.AreEqual(0, mapper.Map(Custom(RockBotEvents.Resync, """{"reason":"expired"}""")).Count);
        Assert.AreEqual(0, mapper.Map(Custom(RockBotEvents.McpSignInStatus, """{"serverName":"x","state":"required"}""")).Count);
        Assert.AreEqual(0, mapper.Map(Custom("rockbot.something_new", "{}")).Count);
    }

    [TestMethod]
    public void Notify_StatusEvents_MapToTheirChatEvents()
    {
        var mapper = AgUiEventMapper.ForNotify(Thread);
        mapper.AgentVersion = "1.4.0";

        var named = (AgentInfoReceived)mapper.Map(Custom(RockBotEvents.AgentName, """{"agentName":"DockerBot"}""")).Single();
        Assert.AreEqual(("DockerBot", "1.4.0"), (named.Name, named.Version));
        Assert.AreEqual("DockerBot", mapper.PrimaryAgentName);

        Assert.IsInstanceOfType<ConversationCleared>(
            mapper.Map(Custom(RockBotEvents.ContextCleared, """{"threadId":"agui-session"}""")).Single());

        var work = (ActiveStatusReconciled)mapper.Map(Custom(RockBotEvents.ActiveWork, """{"work":[]}""")).Single();
        Assert.IsFalse(work.Status.IsProcessing);
        Assert.AreEqual(0, work.Status.Work!.Count);

        var finished = (LateRunFinished)mapper.Map(Custom(RockBotEvents.RunFinished, """{"runId":"r1","outcome":"error","error":"x"}""")).Single();
        Assert.AreEqual(("r1", "error"), (finished.TurnId, finished.Outcome));
    }

    // ── MCP questions ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task AQuestionInARun_ThenItsAnswerOnTheNotifyStream_ShowAFormThatOpensAndCloses()
    {
        var (run, asked) = await RunAsync("run-2", "Book it", Recorded.RunWithQuestion);

        var question = run.OfType<McpQuestionReceived>().Single();
        Assert.AreEqual("q-bde073c5", question.Form!.QuestionId);
        Assert.AreEqual("scheduler", question.Form.ServerName);
        CollectionAssert.AreEqual(
            new[] { "tz:string", "length:single-select", "notify:boolean" },
            question.Form.Fields.Select(f => $"{f.Name}:{f.Kind}").ToArray());
        Assert.IsTrue(question.Form.Fields[0].Required);
        Assert.AreEqual("One hour", question.Form.Fields[1].Options![1].Label);
        Assert.AreEqual("\"30\"", question.Form.Fields[1].Default);
        Assert.AreEqual("scheduler asks: Which time zone should the meeting be in?", question.Reply.Content);
        Assert.AreEqual(McpQuestionForm.ContentType, question.Reply.ContentType);

        // The run ends plainly in custom mode: the form is open and the user can keep chatting.
        Assert.IsNull(asked.CurrentTurn);
        var form = asked.FindQuestion("q-bde073c5")!;
        Assert.AreEqual(MessageCategory.McpQuestion, form.Category);
        Assert.IsTrue(form.IsQuestionOpen(DateTimeOffset.UtcNow));
        Assert.AreEqual(MessageCategory.PrimaryFinal, asked.Messages[^1].Category);

        // The user submits; the status and the agent's reply arrive on the notify stream.
        var at = new DateTime(2026, 11, 14, 21, 0, 0, DateTimeKind.Utc);
        var submitted = ChatReducer.Reduce(asked, new McpAnswerSubmitted("q-bde073c5") { Timestamp = at });
        Assert.IsTrue(submitted.IsBusy(at), "busy until the agent's follow-up arrives");

        var notify = await MapAsync(AgUiEventMapper.ForNotify(Thread), Recorded.NotifyQuestionAnswered);
        CollectionAssert.AreEqual(
            new[] { typeof(McpQuestionStatusChanged), typeof(ReplyReceived) },
            notify.Select(e => e.GetType()).ToArray());
        Assert.AreEqual("q-bde073c5", ((ReplyReceived)notify[1]).Reply.FollowsQuestionId);

        var answered = ChatReducer.Replay(notify, submitted);
        var closed = answered.FindQuestion("q-bde073c5")!;
        Assert.AreEqual(McpQuestionState.Answered, closed.QuestionState);
        Assert.AreEqual("user", closed.QuestionAnsweredBy);
        Assert.IsFalse(closed.IsSubmitting);
        Assert.IsFalse(answered.IsBusy(at), "the follow-up ended the wait");
        Assert.AreEqual("Booked for Brussels time.", answered.Messages[^1].Content);
    }

    [TestMethod]
    public void AQuestionListedByTheQuestionsEndpoint_IsTheSameChatEvent()
    {
        using var value = JsonDocument.Parse(Recorded.QuestionValue);

        var question = AgUiEventMapper.Question(value.RootElement)!;

        Assert.AreEqual("q-bde073c5", question.Form!.QuestionId);
        Assert.AreEqual("agui-session", question.Reply.SessionId);
    }

    [TestMethod]
    public void AQuestionWhoseFormCantBeRead_IsShownAsItsText()
    {
        using var value = JsonDocument.Parse("""{"sessionId":"agui-session","agentName":"mcp-bridge","form":{"fields":"?"},"text":"scheduler asks: which one?"}""");

        var question = AgUiEventMapper.Question(value.RootElement)!;

        Assert.IsNull(question.Form);
        Assert.AreEqual("scheduler asks: which one?", question.Reply.Content);
    }

    // ── History ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void History_ReplacesTheTranscript_AndKeepsAttachmentUrls()
    {
        using var snapshot = JsonDocument.Parse(Recorded.History);
        var urls = new Dictionary<string, string>();

        var loaded = AgUiEventMapper.History(snapshot.RootElement, Thread, urls);
        var state = ChatReducer.Replay([new AgentInfoReceived("RockBot", "demo"), loaded with { IdPrefix = "h" }]);

        Assert.AreEqual(2, state.Messages.Count);
        Assert.IsTrue(state.Messages[0].IsFromUser);
        Assert.AreEqual("an image please", state.Messages[0].Content);
        Assert.AreEqual(new DateTime(2026, 11, 14, 20, 44, 6, DateTimeKind.Utc).Date, state.Messages[0].Timestamp.Date);
        Assert.AreEqual("RockBot", state.Messages[1].AgentName);
        Assert.AreEqual("demo-fd34.png", state.Messages[1].Attachments!.Single().Path);
        Assert.AreEqual("/attachments/demo-fd34.png", urls["demo-fd34.png"]);
    }

    [TestMethod]
    public void History_LeavesOutSystemTurns_AndShowsAStoredSubagentResultAsTheSubagents()
    {
        using var snapshot = JsonDocument.Parse("""
            {"type":"MESSAGES_SNAPSHOT","messages":[
              {"id":"t-0","role":"user","content":"Research the vendors","metadata":{"rockbot":{"timestamp":"2026-11-14T09:00:00+00:00"}}},
              {"id":"t-1","role":"user","content":"[Subagent task t1 completed]: Vendor B","metadata":{"rockbot":{"timestamp":"2026-11-14T09:01:00+00:00","agentName":"subagent-t1"}}},
              {"id":"t-2","role":"system","content":"[Consolidate the findings]","metadata":{"rockbot":{"timestamp":"2026-11-14T09:01:01+00:00"}}},
              {"id":"t-3","role":"assistant","name":"RockBot","content":"Vendor B is cheapest.","metadata":{"rockbot":{"timestamp":"2026-11-14T09:01:05+00:00","agentName":"RockBot"}}}]}
            """);

        var state = ChatReducer.Replay(
            [new AgentInfoReceived("RockBot", "1.4.0"), AgUiEventMapper.History(snapshot.RootElement, Thread)]);

        CollectionAssert.AreEqual(
            new[] { MessageCategory.UserInput, MessageCategory.SubagentActivity, MessageCategory.PrimaryFinal },
            state.Messages.Select(m => m.Category).ToArray());
        Assert.IsFalse(state.Messages[1].IsFromUser, "a result the agent stored as its own input is not the user's words");
        Assert.IsFalse(state.Messages.Any(m => m.Content.Contains("Consolidate")), "a prompt the agent wrote to itself is not shown");
    }

    [TestMethod]
    public void History_RestoresIdsSessionsAndAnchors()
    {
        // #809: what a reply's rockbot.origin carried live is in its history metadata.
        using var snapshot = JsonDocument.Parse("""
            {"type":"MESSAGES_SNAPSHOT","messages":[
              {"id":"turn-a","role":"user","content":"hello","metadata":{"rockbot":{"timestamp":"2026-11-14T09:00:00+00:00","sessionId":"agui-session"}}},
              {"id":"turn-b","role":"assistant","name":"RockBot","content":"Nothing needs your attention today.","metadata":{"rockbot":{"timestamp":"2026-11-14T09:05:00+00:00","agentName":"RockBot","sessionId":"scheduled","origin":{"channel":"scheduler","promptSummary":"Daily summary","startedAt":"2026-11-14T09:00:00+00:00","sessionId":"scheduled"},"source":{"kind":"scheduled"}}}}]}
            """);
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-14T09:10:00Z"));

        var loaded = AgUiEventMapper.History(snapshot.RootElement, Thread, clock: clock);
        var state = ChatReducer.Replay([new AgentInfoReceived("RockBot", "1.4.0"), loaded]);

        CollectionAssert.AreEqual(new[] { "turn-a", "turn-b" }, state.Messages.Select(m => m.MessageId).ToArray());
        var report = state.Messages[1];
        Assert.AreEqual(MessageCategory.ScheduledUser, report.Category);
        Assert.AreEqual("scheduled", report.SessionId);
        StringAssert.StartsWith(report.Content, "> ↳ Re: \"Daily summary\"");
        StringAssert.Contains(report.Content, "from scheduler · 10m ago");
        StringAssert.EndsWith(report.Content, "\n\nNothing needs your attention today.");
        Assert.AreEqual("hello", state.Messages[0].Content, "a turn without an origin has no anchor");
    }

    // ── The proxy's vocabulary ───────────────────────────────────────────────

    /// <summary>
    /// The client repeats the proxy's event names (the library that defines them can't be
    /// referenced from a browser app). This fails when the proxy gains or renames one.
    /// </summary>
    [TestMethod]
    public void TheClientsEventNames_AreTheProxys()
    {
        static Dictionary<string, string> Constants(Type type) => type
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        var proxy = Constants(typeof(TurnEventMapper.CustomNames));
        var client = Constants(typeof(RockBotEvents));

        foreach (var (name, value) in proxy)
        {
            // Only the CLI emits this one (a file it sent); the proxy never does.
            if (name == nameof(TurnEventMapper.CustomNames.AttachmentSent))
                continue;
            Assert.IsTrue(client.TryGetValue(name, out var mine), $"RockBotEvents has no {name}");
            Assert.AreEqual(value, mine, name);
        }

        Assert.AreEqual(TurnEventMapper.ProgressActivityType, RockBotEvents.ProgressActivityType);
        Assert.AreEqual(TurnEventMapper.PrimaryTaskListKey, RockBotEvents.PrimaryTaskListKey);
        Assert.AreEqual(TurnEventMapper.ErrorCodes.AgentLost, RockBotEvents.ErrorCodes.AgentLost);
        Assert.AreEqual(TurnEventMapper.ErrorCodes.Error, RockBotEvents.ErrorCodes.Error);
        Assert.AreEqual(RockBot.Host.WellKnownSessions.ScheduledSystem, RockBotEvents.Sessions.ScheduledSystem);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CustomEvent Custom(string name, string value) =>
        new() { Name = name, Value = JsonDocument.Parse(value).RootElement.Clone() };

    private static ActivitySnapshotEvent Activity(string id, string agentName, string text) => new()
    {
        MessageId = id,
        ActivityType = RockBotEvents.ProgressActivityType,
        Content = JsonSerializer.SerializeToElement(new { agentName, text }),
        Replace = true,
    };

    private static IEnumerable<BaseEvent> Message(string id, string text, string? origin)
    {
        yield return new TextMessageStartEvent { MessageId = id, Role = "assistant", Name = "RockBot" };
        yield return new TextMessageContentEvent { MessageId = id, Delta = text };
        yield return new TextMessageEndEvent { MessageId = id };
        if (origin is not null)
            yield return Custom(RockBotEvents.Origin, origin);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
