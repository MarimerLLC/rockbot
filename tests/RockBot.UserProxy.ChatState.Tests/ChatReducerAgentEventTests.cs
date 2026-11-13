using static RockBot.UserProxy.ChatState.Tests.ChatEventFixtures;

namespace RockBot.UserProxy.ChatState.Tests;

/// <summary>Reducer replays for the structured agent events (#795).</summary>
[TestClass]
public class ChatReducerAgentEventTests
{
    private static ToolCallInfo Call(string id, string phase, string? result = null, long? ms = null, string? error = null) => new()
    {
        ToolCallId = id,
        ToolCallName = "web_search",
        Phase = phase,
        ArgsSummary = "web_search(query: rockbot)",
        Result = result,
        DurationMs = ms,
        Error = error,
    };

    private static TaskListItem Item(string id, string description, string status) =>
        new() { Id = id, Description = description, Status = status };

    // ── Tool calls ───────────────────────────────────────────────────────

    [TestMethod]
    public void ToolCall_IsUpdatedInPlace_ByItsCallId()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new ToolCallReported(MessageCategory.PrimaryProgress, null, Call("c1", ToolCallPhase.Started), "Calling web_search…")
                { Timestamp = At(1), MessageId = "log" },
            new ToolCallReported(MessageCategory.PrimaryProgress, null, Call("c2", ToolCallPhase.Started), "Calling read_page…")
                { Timestamp = At(2) },
            new ToolCallReported(MessageCategory.PrimaryProgress, null, Call("c1", ToolCallPhase.Finished, "3 results", 420), "web_search → 3 results")
                { Timestamp = At(3) },
            new ToolCallReported(MessageCategory.PrimaryProgress, null, Call("c2", ToolCallPhase.Failed, error: "404", ms: 90), "read_page failed: 404")
                { Timestamp = At(4) },
        ]);

        var log = state.FindMessage("log")!;
        Assert.AreEqual(2, log.ActivityLogEntries.Count, "one entry per call, not per report");

        var first = log.ActivityLogEntries[0];
        Assert.AreEqual(ToolCallPhase.Finished, first.ToolCall?.Phase);
        Assert.AreEqual(420L, first.ToolCall?.DurationMs);
        Assert.AreEqual("web_search → 3 results", first.Content);
        Assert.AreEqual(At(1), first.Timestamp, "the entry keeps the time the call started");

        Assert.AreEqual(ToolCallPhase.Failed, log.ActivityLogEntries[1].ToolCall?.Phase);
        Assert.AreEqual("404", log.ActivityLogEntries[1].ToolCall?.Error);
        Assert.AreEqual("read_page failed: 404", log.Content, "the summary line is the latest report");
    }

    [TestMethod]
    public void ToolCall_MixesWithPlainProgressInTheSameLog()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnProgress("turn-1", "I'm working on that") { Timestamp = At(1), MessageId = "log" },
            new ToolCallReported(MessageCategory.PrimaryProgress, null, Call("c1", ToolCallPhase.Started), "Calling web_search…")
                { Timestamp = At(2) },
        ]);

        var log = state.FindMessage("log")!;
        Assert.AreEqual(2, log.ActivityLogEntries.Count);
        Assert.IsNull(log.ActivityLogEntries[0].ToolCall);
        Assert.AreEqual("c1", log.ActivityLogEntries[1].ToolCall?.ToolCallId);
    }

    // ── Subagent and worker lifecycle ────────────────────────────────────

    [TestMethod]
    public void SubagentStartedAndFinished_OpenAndCloseItsLog()
    {
        var started = ChatReducer.Replay(
        [
            new SubagentRunStarted(new SubagentStarted
            {
                SubagentRunId = "abc", Kind = ReplySourceKind.Subagent, Description = "research", ThreadId = Session,
                StartedAt = At(0),
            }) { Timestamp = At(0), MessageId = "sub-log" },
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-abc", "searching") { Timestamp = At(1) },
        ]);

        Assert.IsTrue(started.IsActivityLogOpen("sub-log"));
        Assert.AreEqual(2, started.FindMessage("sub-log")!.ActivityLogEntries.Count, "its progress lands in the same log");

        var finished = ChatReducer.Reduce(started, new SubagentRunFinished(new SubagentFinished
        {
            SubagentRunId = "abc", Kind = ReplySourceKind.Subagent, ThreadId = Session,
            Outcome = SubagentOutcome.Failed, Error = "LLM unavailable", FinishedAt = At(9),
        }) { Timestamp = At(9) });

        Assert.IsFalse(finished.IsActivityLogOpen("sub-log"));
        Assert.AreEqual("Failed: LLM unavailable", finished.FindMessage("sub-log")!.Content);
        Assert.AreEqual(0, finished.ActiveStatusIndicators.Count);
    }

    [TestMethod]
    public void WorkerStarted_OpensAWorkerLog_FinishedAfterItsResult_ChangesNothing()
    {
        var state = ChatReducer.Replay(
        [
            new SubagentRunStarted(new SubagentStarted
            {
                SubagentRunId = "w1", Kind = ReplySourceKind.Worker, Description = "summarize", ThreadId = Session,
                StartedAt = At(0),
            }) { Timestamp = At(0), MessageId = "worker-log" },
            new ActivityReported(MessageCategory.SubagentActivity, "worker-w1", "Done", IsCompletion: true) { Timestamp = At(5) },
        ]);

        Assert.AreEqual("worker-w1", state.FindMessage("worker-log")!.AgentName);
        Assert.IsFalse(state.IsActivityLogOpen("worker-log"));

        var after = ChatReducer.Reduce(state, new SubagentRunFinished(new SubagentFinished
        {
            SubagentRunId = "w1", Kind = ReplySourceKind.Worker, ThreadId = Session,
            Outcome = SubagentOutcome.Success, FinishedAt = At(6),
        }) { Timestamp = At(6) });

        Assert.AreEqual(state.Messages, after.Messages, "a log already closed by its result isn't reopened");
    }

    [TestMethod]
    public void ActiveStatusReconciled_WithWork_KeepsWorkerLogs()
    {
        var state = ChatReducer.Replay(
        [
            new ActivityReported(MessageCategory.SubagentActivity, "worker-w1", "summarizing") { Timestamp = At(0), MessageId = "w" },
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-gone", "old") { Timestamp = At(0), MessageId = "gone" },
            new ActiveStatusReconciled(new ActiveStatusResponse
            {
                IsProcessing = false,
                Subagents = [new ActiveSubagentInfo { TaskId = "s2", Description = "research", StartedAt = At(1) }],
                Work =
                [
                    new ActiveWorkItem { TaskId = "w1", Kind = ReplySourceKind.Worker, Description = "summarize", StartedAt = At(0) },
                    new ActiveWorkItem { TaskId = "s2", Kind = ReplySourceKind.Subagent, Description = "research", StartedAt = At(1) },
                ],
            }) { Timestamp = At(2), IdPrefix = "seed" },
        ]);

        Assert.IsTrue(state.IsActivityLogOpen("w"), "a running worker keeps its log");
        Assert.IsFalse(state.IsActivityLogOpen("gone"));
        Assert.AreEqual("subagent-s2", state.FindMessage("seed-s2")?.AgentName);
        Assert.IsTrue(state.IsActivityLogOpen("seed-s2"));
    }

    // ── Task lists ───────────────────────────────────────────────────────

    [TestMethod]
    public void TaskList_SnapshotThenDelta_TrackedPerSource()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TaskListSnapshotReceived(new TaskListSnapshot
            {
                ThreadId = Session, RunId = "turn-1", Source = ReplySource.Primary,
                Items = [Item("1", "Search", "pending"), Item("2", "Summarize", "pending")],
            }) { Timestamp = At(1) },
            new TaskListDeltaReceived(new TaskListDelta
            {
                ThreadId = Session, RunId = "turn-1", Source = ReplySource.Primary, Item = Item("1", "Search", "completed"),
            }) { Timestamp = At(2) },
            new TaskListDeltaReceived(new TaskListDelta
            {
                ThreadId = Session, RunId = "turn-1", Source = ReplySource.Primary, Item = Item("2", "Summarize", "in_progress"),
            }) { Timestamp = At(3) },
            new TaskListSnapshotReceived(new TaskListSnapshot
            {
                ThreadId = Session, Source = ReplySource.Subagent("abc"), Items = [Item("a", "Read docs", "pending")],
            }) { Timestamp = At(4) },
        ]);

        var primary = state.TaskLists[ReplySourceKind.Primary];
        Assert.AreEqual(1, primary.CompletedCount);
        Assert.AreEqual("Summarize", primary.CurrentItem?.Description);
        Assert.AreEqual("turn-1", primary.RunId);
        Assert.AreEqual(1, state.TaskLists["abc"].Items.Count);

        // A new turn drops the last turn's plan; the subagent's plan goes when it finishes.
        var next = ChatReducer.Replay(
        [
            new TurnStarted("turn-2") { Timestamp = At(5) },
            new SubagentRunFinished(new SubagentFinished
            {
                SubagentRunId = "abc", Kind = ReplySourceKind.Subagent, ThreadId = Session,
                Outcome = SubagentOutcome.Success, FinishedAt = At(6),
            }) { Timestamp = At(6) },
        ], state);

        Assert.AreEqual(0, next.TaskLists.Count);
    }

    [TestMethod]
    public void ToolCall_KeepsItsArguments_WhenTheCompletionOmitsThem()
    {
        // Only a call's start carries its arguments.
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new ToolCallReported(MessageCategory.PrimaryProgress, null, Call("c1", ToolCallPhase.Started), "Calling web_search…")
                { Timestamp = At(1), MessageId = "log" },
            new ToolCallReported(MessageCategory.PrimaryProgress, null,
                Call("c1", ToolCallPhase.Finished, "3 results", 420) with { ArgsSummary = null }, "web_search → 3 results")
                { Timestamp = At(2) },
        ]);

        var call = state.FindMessage("log")!.ActivityLogEntries.Single().ToolCall!;
        Assert.AreEqual(ToolCallPhase.Finished, call.Phase);
        Assert.AreEqual("web_search(query: rockbot)", call.ArgsSummary);
    }

    [TestMethod]
    public void LateReports_ForAFinishedWorker_DontReopenItsLog()
    {
        var worker = ChatReducer.SubagentAgentName(ReplySourceKind.Worker, "w1");
        var state = ChatReducer.Replay(
        [
            new SubagentRunStarted(new SubagentStarted
            {
                SubagentRunId = "w1", Kind = ReplySourceKind.Worker, Description = "gather", ThreadId = Session, StartedAt = At(0),
            }) { Timestamp = At(0), MessageId = "worker-log" },
            new ToolCallReported(MessageCategory.SubagentActivity, worker, Call("c1", ToolCallPhase.Started), "Calling web_search…")
                { Timestamp = At(1) },
            new SubagentRunFinished(new SubagentFinished
            {
                SubagentRunId = "w1", Kind = ReplySourceKind.Worker, ThreadId = Session, Outcome = SubagentOutcome.Success, FinishedAt = At(2),
            }) { Timestamp = At(2) },
            // Progress travels on another topic than the lifecycle, so these trail the finish.
            new ToolCallReported(MessageCategory.SubagentActivity, worker, Call("c1", ToolCallPhase.Finished, "3 results", 300) with { ArgsSummary = null },
                "web_search → 3 results") { Timestamp = At(3) },
            new ToolCallReported(MessageCategory.SubagentActivity, worker, Call("c2", ToolCallPhase.Started), "Calling read_page…")
                { Timestamp = At(4) },
            new ActivityReported(MessageCategory.SubagentActivity, worker, "halfway there", IsCompletion: false)
                { Timestamp = At(5) },
        ]);

        Assert.AreEqual(1, state.Messages.Count(m => m.AgentName == worker), "no second log for the finished worker");
        Assert.IsFalse(state.OpenActivityLogs.Values.Contains("worker-log"), "its log stays closed");
        var log = state.FindMessage("worker-log")!;
        var call = log.ActivityLogEntries.Single(e => e.ToolCall is not null).ToolCall!;
        Assert.AreEqual(ToolCallPhase.Finished, call.Phase, "the trailing report still completes its card");
        Assert.AreEqual("web_search(query: rockbot)", call.ArgsSummary);
    }

    [TestMethod]
    public void TaskList_FromAnOlderRun_DoesntTouchTheCurrentPlan()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-2") { Timestamp = At(0) },
            new TaskListSnapshotReceived(new TaskListSnapshot
            {
                ThreadId = Session, RunId = "turn-2", Source = ReplySource.Primary,
                Items = [Item("1", "Search", "pending")],
            }) { Timestamp = At(1) },
            // turn-1 timed out earlier; its events still trickle in.
            new TaskListSnapshotReceived(new TaskListSnapshot
            {
                ThreadId = Session, RunId = "turn-1", Source = ReplySource.Primary,
                Items = [Item("1", "Old step", "pending")],
            }) { Timestamp = At(2) },
            new TaskListDeltaReceived(new TaskListDelta
            {
                ThreadId = Session, RunId = "turn-1", Source = ReplySource.Primary, Item = Item("1", "Old step", "completed"),
            }) { Timestamp = At(3) },
        ]);

        var plan = state.TaskLists[ReplySourceKind.Primary];
        Assert.AreEqual("turn-2", plan.RunId);
        Assert.AreEqual("Search", plan.Items.Single().Description);
        Assert.AreEqual("pending", plan.Items.Single().Status);
    }

    [TestMethod]
    [DataRow(ReplySourceKind.Scheduled)]
    [DataRow(ReplySourceKind.A2A)]
    [DataRow(ReplySourceKind.Primary)]
    public void TaskList_WithoutARunId_IsNotShownAsThePlan(string kind)
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TaskListSnapshotReceived(new TaskListSnapshot
            {
                ThreadId = "scheduled", Source = new ReplySource { Kind = kind }, Items = [Item("1", "Background step", "pending")],
            }) { Timestamp = At(1) },
        ]);

        Assert.AreEqual(0, state.TaskLists.Count);
    }
    // ── Heartbeats and outcomes ──────────────────────────────────────────

    [TestMethod]
    public void Heartbeat_SetsThinkingText_ForTheRunningTurnOnly()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnHeartbeat("turn-1", "Calling web_search") { Timestamp = At(15) },
        ]);
        Assert.AreEqual("Calling web_search", state.ThinkingMessage);

        var other = ChatReducer.Apply(state, new TurnHeartbeat("turn-0", "stale") { Timestamp = At(16) });
        Assert.IsFalse(other.Accepted);
        Assert.AreEqual("Calling web_search", other.State.ThinkingMessage);

        var silent = ChatReducer.Reduce(state, new TurnHeartbeat("turn-1", null) { Timestamp = At(30) });
        Assert.AreEqual("Calling web_search", silent.ThinkingMessage, "a heartbeat with no status keeps the last one");
    }

    [TestMethod]
    public void LateHeartbeat_AfterATimeout_ShowsTheAgentStillWorking_UntilTheRunFinishes()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnEnded("turn-1", TurnOutcome.TimedOut) { Timestamp = At(180) },
            new TurnHeartbeat("turn-1", null) { Timestamp = At(195) },
        ]);
        Assert.AreEqual("Still working…", state.ThinkingMessage);

        var done = ChatReducer.Reduce(state, new LateRunFinished("turn-1", RunOutcome.Cancelled) { Timestamp = At(200) });
        Assert.IsNull(done.ThinkingMessage);
    }

    [TestMethod]
    public void AgentLost_ShowsTheError_StopsTheSpinner_EndsTheTurn()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnProgress("turn-1", "Calling web_search…") { Timestamp = At(1), MessageId = "log" },
            new TurnHeartbeat("turn-1", "Calling web_search") { Timestamp = At(15) },
            new TurnEnded("turn-1", TurnOutcome.AgentLost) { Error = "Nothing heard for 45 seconds.", Timestamp = At(60) },
        ]);

        var error = state.Messages[^1];
        Assert.IsTrue(error.IsError);
        StringAssert.StartsWith(error.Content, "Lost contact with the agent");
        Assert.IsFalse(state.IsActivityLogOpen("log"));
        Assert.IsNull(state.ThinkingMessage);
        Assert.IsFalse(state.IsBusy(At(61)));
    }

    [TestMethod]
    public void AgentCancelled_ShowsANotice_ButNotAfterTheUserCancelled()
    {
        var byAgent = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnEnded("turn-1", TurnOutcome.Cancelled) { Timestamp = At(5) },
        ]);
        Assert.AreEqual("Turn cancelled", byAgent.Messages.Single().Content);
        Assert.IsFalse(byAgent.IsBusy(At(6)));

        var byUser = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new UserCancelled { Timestamp = At(4) },
            new TurnEnded("turn-1", TurnOutcome.Cancelled) { Timestamp = At(5) },
        ]);
        Assert.AreEqual(0, byUser.Messages.Count);
    }

    [TestMethod]
    public void AgentError_ShowsTheAgentsReply_OrItsErrorText()
    {
        var withReply = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnEnded("turn-1", TurnOutcome.AgentError)
            {
                Reply = Reply("Sorry, I ran into an error"), Error = "boom", Timestamp = At(5),
            },
        ]);
        Assert.AreEqual(MessageCategory.PrimaryFinal, withReply.Messages.Single().Category);
        Assert.IsFalse(withReply.IsBusy(At(6)));

        var withoutReply = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnEnded("turn-1", TurnOutcome.AgentError) { Error = "boom", Timestamp = At(5) },
        ]);
        Assert.AreEqual("Error: boom", withoutReply.Messages.Single().Content);
        Assert.IsFalse(withoutReply.IsBusy(At(6)));
    }
}
