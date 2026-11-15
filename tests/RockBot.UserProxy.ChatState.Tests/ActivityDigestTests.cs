using static RockBot.UserProxy.ChatState.Tests.ChatEventFixtures;

namespace RockBot.UserProxy.ChatState.Tests;

/// <summary>The activity panel's projection of the chat (#828).</summary>
[TestClass]
public class ActivityDigestTests
{
    private static TaskListItem Item(string id, string description, string status = TaskListStatus.Pending) =>
        new() { Id = id, Description = description, Status = status };

    private static SubagentRunStarted Started(string taskId, double at, string kind = ReplySourceKind.Subagent) =>
        new(new SubagentStarted
        {
            SubagentRunId = taskId, Kind = kind, Description = "research", ThreadId = Session, StartedAt = At(at),
        }) { Timestamp = At(at), MessageId = $"log-{taskId}" };

    private static SubagentRunFinished Finished(string taskId, double at, string kind = ReplySourceKind.Subagent) =>
        new(new SubagentFinished
        {
            SubagentRunId = taskId, Kind = kind, ThreadId = Session, Outcome = SubagentOutcome.Success, FinishedAt = At(at),
        }) { Timestamp = At(at) };

    // ── Subagents ────────────────────────────────────────────────────────

    [TestMethod]
    public void RunningSubagent_HasItsLogStatusAndPlan()
    {
        var state = ChatReducer.Replay(
        [
            Started("abc", 0),
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-abc", "Reading RFC 9438") { Timestamp = At(1) },
            new TaskListSnapshotReceived(new TaskListSnapshot
            {
                ThreadId = Session, Source = ReplySource.Subagent("abc"),
                Items = [Item("a", "Read RFCs", TaskListStatus.Completed), Item("b", "Compare")],
            }) { Timestamp = At(2) },
        ]);

        var running = ActivityDigest.From(state, At(3)).Subagents.Running.Single();

        Assert.AreEqual("log-abc", running.Id);
        Assert.AreEqual("abc", running.DisplayName);
        Assert.IsTrue(running.IsRunning);
        Assert.IsNull(running.FinishedAt);
        Assert.AreEqual("Reading RFC 9438", running.Status);
        Assert.AreEqual(At(0), running.StartedAt);
        Assert.AreEqual(1, running.Plan?.CompletedCount);
        Assert.AreEqual("Compare", running.Plan?.CurrentItem?.Description);
        Assert.AreEqual(2, running.Log?.ActivityLogEntries.Count);
    }

    [TestMethod]
    public void FinishedSubagent_PairsItsResultWithItsLog_BySource()
    {
        // The result is named after RockBot, not the subagent: only its source ties it to the run.
        var state = ChatReducer.Replay(
        [
            Started("abc", 0),
            Finished("abc", 5),
            new ReplyReceived(Reply("BBR wins on lossy links.") with { Source = ReplySource.Subagent("abc") },
                MessageCategory.SubagentActivity) { Timestamp = At(6), MessageId = "result" },
        ]);

        var digest = ActivityDigest.From(state, At(7));

        Assert.AreEqual(0, digest.Subagents.Running.Count);
        var finished = digest.Subagents.Finished.Single();
        Assert.AreEqual("log-abc", finished.Id);
        Assert.AreEqual("result", finished.Result?.MessageId);
        Assert.AreEqual("Finished", finished.Status);
        Assert.AreEqual(At(6), finished.FinishedAt);
        Assert.IsNull(finished.Plan);
    }

    [TestMethod]
    public void ASubagentsResult_ClosesItsLog_EvenWhenNamedAfterRockBot()
    {
        // No lifecycle event: an agent that doesn't send them. The result alone has to close the log.
        var state = ChatReducer.Replay(
        [
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-abc", "searching") { Timestamp = At(0), MessageId = "log-abc" },
            new ReplyReceived(Reply("Findings") with { Source = ReplySource.Subagent("abc") },
                MessageCategory.SubagentActivity) { Timestamp = At(5), MessageId = "result" },
        ]);

        Assert.IsFalse(state.IsActivityLogOpen("log-abc"));
        Assert.AreEqual(0, state.ActiveStatusIndicators.Count);
        Assert.AreEqual("result", ActivityDigest.From(state, At(6)).Subagents.Finished.Single().Result?.MessageId);
    }

    [TestMethod]
    public void ReloadedSubagentResults_PairBySource_NotByName()
    {
        // History brings back two runs' results, both named after RockBot.
        var state = ChatReducer.Reduce(ChatViewState.Empty with { AgentName = Agent }, new HistoryLoaded(
        [
            new ConversationHistoryTurn
            {
                Role = "assistant", Content = "First findings", Timestamp = At(1), AgentName = Agent, Id = "r1",
                Source = ReplySource.Subagent("abc"), SessionId = Session,
            },
            new ConversationHistoryTurn
            {
                Role = "assistant", Content = "Second findings", Timestamp = At(2), AgentName = Agent, Id = "r2",
                Source = ReplySource.Subagent("def"), SessionId = Session,
            },
        ], Session) { Timestamp = At(3) });

        var finished = ActivityDigest.From(state, At(4)).Subagents.Finished;

        Assert.AreEqual("subagent-abc", state.FindMessage("r1")?.RunAgentName);
        CollectionAssert.AreEqual(new[] { "def", "abc" }, finished.Select(i => i.DisplayName).ToArray(), "one item per run");
    }

    [TestMethod]
    public void AResultWithNoLog_IsAFinishedItemOnItsOwn()
    {
        // After a reload: history brings back the result but not the log.
        var state = ChatReducer.Replay(
        [
            new ReplyReceived(Reply("Findings", agentName: "worker-w1"), MessageCategory.SubagentActivity) { Timestamp = At(4), MessageId = "result" },
        ]);

        var finished = ActivityDigest.From(state, At(5)).Subagents.Finished.Single();

        Assert.AreEqual("result", finished.Id);
        Assert.IsTrue(finished.IsWorker);
        Assert.AreEqual("worker w1", finished.DisplayName);
        Assert.IsNull(finished.Log);
        Assert.AreEqual(At(4), finished.FinishedAt);
    }

    [TestMethod]
    public void Finished_IsMostRecentFirst_AndCapped()
    {
        var events = new List<ChatEvent>();
        for (var i = 0; i < 7; i++)
        {
            events.Add(Started($"t{i}", i * 10));
            events.Add(Finished($"t{i}", i * 10 + 5));
        }

        var finished = ActivityDigest.From(ChatReducer.Replay(events), At(100), maxFinished: 3).Subagents.Finished;

        CollectionAssert.AreEqual(new[] { "t6", "t5", "t4" }, finished.Select(i => i.DisplayName).ToArray());
    }

    // ── RockBot's own turns ──────────────────────────────────────────────

    [TestMethod]
    public void BusyTurn_IsRunning_WithTheLiveStatusAndThePlan()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnProgress("turn-1", "Planning") { Timestamp = At(1), MessageId = "primary-log" },
            new TurnHeartbeat("turn-1", "Comparing the two release branches…") { Timestamp = At(2) },
            new TaskListSnapshotReceived(new TaskListSnapshot
            {
                ThreadId = Session, RunId = "turn-1", Source = ReplySource.Primary, Items = [Item("1", "Diff changelogs")],
            }) { Timestamp = At(3) },
        ]);

        var running = ActivityDigest.From(state, At(4)).Agent.Running.Single();

        Assert.AreEqual("primary-log", running.Id);
        Assert.AreEqual("Comparing the two release branches…", running.Status, "the live status text, not the log's last line");
        Assert.AreEqual("Diff changelogs", running.Plan?.CurrentItem?.Description);
    }

    [TestMethod]
    public void BusyTurn_WithNothingLoggedYet_IsStillRunning()
    {
        var state = ChatReducer.Replay([new TurnStarted("turn-1") { Timestamp = At(0) }]);

        var running = ActivityDigest.From(state, At(1)).Agent.Running.Single();

        Assert.AreEqual("turn-1", running.Id);
        Assert.AreEqual(At(0), running.StartedAt);
        Assert.IsNull(running.Log);
    }

    [TestMethod]
    public void BusyWithAFormAnswer_StartsWhenTheAnswerWasSent()
    {
        var state = ChatReducer.Replay(
        [
            Question("q1", atSeconds: 0),
            new McpAnswerSubmitted("q1") { Timestamp = At(10) },
        ]);

        var running = ActivityDigest.From(state, At(40)).Agent.Running.Single();

        Assert.AreEqual(At(10), running.StartedAt);
    }

    [TestMethod]
    public void TheDigest_IsCachedPerState_UntilTheBusyStateChanges()
    {
        var state = ChatReducer.Replay(
        [
            Question("q1", atSeconds: 0),
            new McpAnswerSubmitted("q1") { Timestamp = At(10) },
        ]);

        var first = ActivityDigest.From(state, At(20));
        var again = ActivityDigest.From(state, At(30));
        var expired = ActivityDigest.From(state, At(10) + ChatViewState.FormWaitLimit);

        Assert.AreSame(first, again);
        Assert.AreNotSame(first, expired, "the form wait ran out, so the agent is no longer busy");
        Assert.AreEqual(0, expired.Agent.Running.Count);
    }

    [TestMethod]
    public void FinishedTurn_PairsItsLogWithTheReply()
    {
        var state = ChatReducer.Replay(
        [
            new UserMessageSent("Hi", "user", Session) { Timestamp = At(0) },
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnProgress("turn-1", "Thinking") { Timestamp = At(1), MessageId = "primary-log" },
            new TurnEnded("turn-1", TurnOutcome.Completed) { Reply = Reply("Hello!"), Timestamp = At(3), MessageId = "reply" },
        ]);

        var digest = ActivityDigest.From(state, At(4));

        Assert.AreEqual(0, digest.Agent.Running.Count);
        var finished = digest.Agent.Finished.Single();
        Assert.AreEqual("reply", finished.Result?.MessageId);
        Assert.AreEqual(At(3), finished.FinishedAt);
    }

    // ── A2A and background tasks ─────────────────────────────────────────

    [TestMethod]
    public void InboundA2A_LightsItsIndicatorAndIsRunning()
    {
        var state = ChatReducer.Replay(
        [
            new ActivityReported(MessageCategory.A2AActivity, "PeerAgent", "Working on the request")
                { Timestamp = At(0), MessageId = "a2a-log" },
        ]);

        var indicator = state.ActiveStatusIndicators.Single();
        Assert.AreEqual(MessageCategory.A2AActivity, indicator.Category);
        Assert.AreEqual("a2a-log", indicator.MessageId);
        Assert.AreEqual("PeerAgent", ActivityDigest.From(state, At(1)).A2A.Running.Single().DisplayName);
    }

    [TestMethod]
    public void BackgroundTasks_KeepEachRunOfTheSameTaskApart()
    {
        var state = ChatReducer.Replay(
        [
            new ActivityReported(MessageCategory.ScheduledUser, "daily-brief", "Gathering") { Timestamp = At(0), MessageId = "run-1" },
            new ReplyReceived(Reply("Brief 1", agentName: "daily-brief", sessionId: "scheduled"), MessageCategory.ScheduledUser)
                { Timestamp = At(5), MessageId = "brief-1" },
            new ActivityReported(MessageCategory.ScheduledUser, "daily-brief", "Gathering") { Timestamp = At(100), MessageId = "run-2" },
            new ReplyReceived(Reply("Brief 2", agentName: "daily-brief", sessionId: "scheduled"), MessageCategory.ScheduledUser)
                { Timestamp = At(105), MessageId = "brief-2" },
            new ActivityReported(MessageCategory.ScheduledSystem, "dream", "Consolidating") { Timestamp = At(110), MessageId = "dream-log" },
        ]);

        var scheduled = ActivityDigest.From(state, At(120)).Scheduled;

        Assert.AreEqual("dream-log", scheduled.Running.Single().Id);
        CollectionAssert.AreEqual(new[] { "run-2", "run-1" }, scheduled.Finished.Select(i => i.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "brief-2", "brief-1" }, scheduled.Finished.Select(i => i.Result?.MessageId).ToArray());
    }

    [TestMethod]
    public void AnIdleStatus_ClosesA2AAndBackgroundLogsLeftOpen()
    {
        var state = ChatReducer.Replay(
        [
            new ActivityReported(MessageCategory.A2AActivity, "PeerAgent", "Working") { Timestamp = At(0), MessageId = "a2a-log" },
            new ActivityReported(MessageCategory.ScheduledUser, "daily-brief", "Gathering") { Timestamp = At(1), MessageId = "task-log" },
            new ActiveStatusReconciled(new ActiveStatusResponse { Subagents = [], IsProcessing = false }) { Timestamp = At(2) },
        ]);

        Assert.AreEqual(0, state.ActiveStatusIndicators.Count);
        Assert.AreEqual(0, ActivityDigest.From(state, At(3)).A2A.Running.Count);
    }

    [TestMethod]
    public void ABusyStatus_LeavesA2AAndBackgroundLogsOpen()
    {
        var state = ChatReducer.Replay(
        [
            new ActivityReported(MessageCategory.A2AActivity, "PeerAgent", "Working") { Timestamp = At(0), MessageId = "a2a-log" },
            new ActiveStatusReconciled(new ActiveStatusResponse { Subagents = [], IsProcessing = true }) { Timestamp = At(2) },
        ]);

        Assert.IsTrue(state.IsActivityLogOpen("a2a-log"));
    }

    [TestMethod]
    public void ARunningUserScheduledTask_LightsTheBackgroundIndicator()
    {
        var state = ChatReducer.Replay(
        [
            new ActivityReported(MessageCategory.ScheduledUser, "daily-brief", "Gathering") { Timestamp = At(0), MessageId = "run-1" },
        ]);

        Assert.AreEqual(MessageCategory.ScheduledUser, state.ActiveStatusIndicators.Single().Category);
    }
}
