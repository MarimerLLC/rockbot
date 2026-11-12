using RockBot.Host;
using RockBot.UserProxy;
using RockBot.UserProxy.Blazor.Services;
using RockBot.UserProxy.ChatState;

namespace RockBot.UserProxy.Blazor.Tests;

/// <summary>Categorizing by <see cref="AgentReply.Source"/>, and the structured events (#795).</summary>
[TestClass]
public class BlazorUserFrontendSourceTests
{
    private ChatStateService _chatState = null!;
    private BlazorUserFrontend _frontend = null!;

    [TestInitialize]
    public void Setup()
    {
        _chatState = new ChatStateService();
        _frontend = new BlazorUserFrontend(_chatState);
    }

    private static AgentReply Reply(ReplySource? source, bool isFinal = true, string sessionId = "blazor-session",
        string agentName = "RockBot") => new()
    {
        Content = "content",
        SessionId = sessionId,
        AgentName = agentName,
        IsFinal = isFinal,
        Source = source,
    };

    private ChatMessage Last => _chatState.State.Messages[^1];

    [TestMethod]
    [DataRow(ReplySourceKind.Primary, true, MessageCategory.PrimaryFinal)]
    [DataRow(ReplySourceKind.Primary, false, MessageCategory.PrimaryProgress)]
    [DataRow(ReplySourceKind.Bridge, true, MessageCategory.PrimaryFinal)]
    [DataRow(ReplySourceKind.Subagent, true, MessageCategory.SubagentActivity)]
    [DataRow(ReplySourceKind.Worker, false, MessageCategory.SubagentActivity)]
    [DataRow(ReplySourceKind.A2A, true, MessageCategory.A2AActivity)]
    [DataRow(ReplySourceKind.Scheduled, true, MessageCategory.ScheduledUser)]
    public async Task Source_DecidesTheCategory(string kind, bool isFinal, MessageCategory expected)
    {
        // A name and session that the heuristics would read differently: the source wins.
        var reply = Reply(new ReplySource { Kind = kind, TaskId = "t1" }, isFinal, sessionId: "a2a-inbound",
            agentName: "SomethingElse");

        if (isFinal)
            await _frontend.DisplayReplyAsync(reply);
        else
            await _frontend.DisplayStatusAsync(reply);

        Assert.AreEqual(expected, Last.Category);
    }

    [TestMethod]
    public async Task ScheduledSource_InTheSystemSession_IsScheduledSystem()
    {
        await _frontend.DisplayReplyAsync(Reply(ReplySource.Scheduled, sessionId: WellKnownSessions.ScheduledSystem));

        Assert.AreEqual(MessageCategory.ScheduledSystem, Last.Category);
    }

    [TestMethod]
    public async Task NoSource_FallsBackToTheHeuristics()
    {
        await _frontend.DisplayStatusAsync(Reply(null, isFinal: false, agentName: "worker-abc"));
        Assert.AreEqual(MessageCategory.SubagentActivity, Last.Category);

        await _frontend.DisplayReplyAsync(Reply(null, sessionId: "a2a-inbound"));
        Assert.AreEqual(MessageCategory.A2AActivity, Last.Category);
    }

    [TestMethod]
    public async Task WorkerProgress_LandsInTheLogItsStartEventOpened()
    {
        await _frontend.OnSubagentStartedAsync(new SubagentStarted
        {
            SubagentRunId = "w1", Kind = ReplySourceKind.Worker, Description = "summarize", ThreadId = "s1",
            StartedAt = DateTimeOffset.UtcNow,
        });
        // Progress from an agent that still names it oddly; the source says which log it belongs to.
        await _frontend.DisplayStatusAsync(Reply(ReplySource.Worker("w1"), isFinal: false, agentName: "RockBot"));

        var log = _chatState.State.Messages.Single();
        Assert.AreEqual("worker-w1", log.AgentName);
        Assert.AreEqual(2, log.ActivityLogEntries.Count);

        await _frontend.OnSubagentFinishedAsync(new SubagentFinished
        {
            SubagentRunId = "w1", Kind = ReplySourceKind.Worker, ThreadId = "s1", Outcome = SubagentOutcome.Success,
            FinishedAt = DateTimeOffset.UtcNow,
        });
        Assert.IsFalse(_chatState.State.IsActivityLogOpen(log.MessageId));
    }

    [TestMethod]
    public async Task ToolCallProgress_IsOneEntryPerCall()
    {
        var started = Reply(ReplySource.Subagent("s1"), isFinal: false, agentName: "subagent-s1") with
        {
            ToolCall = new ToolCallInfo { ToolCallId = "c1", ToolCallName = "web_search", Phase = ToolCallPhase.Started },
        };
        await _frontend.DisplayStatusAsync(started);
        await _frontend.DisplayStatusAsync(started with
        {
            Content = "web_search → 3 results",
            ToolCall = started.ToolCall! with { Phase = ToolCallPhase.Finished, Result = "3 results", DurationMs = 12 },
        });

        var entry = _chatState.State.Messages.Single().ActivityLogEntries.Single();
        Assert.AreEqual(ToolCallPhase.Finished, entry.ToolCall?.Phase);
    }

    [TestMethod]
    public async Task TaskListsAndSnapshots_ReachTheChatState()
    {
        await _frontend.OnTaskListSnapshotAsync(new TaskListSnapshot
        {
            ThreadId = "s1", Source = ReplySource.Subagent("s1"),
            Items = [new TaskListItem { Id = "1", Description = "Read", Status = "pending" }],
        });
        await _frontend.OnTaskListDeltaAsync(new TaskListDelta
        {
            ThreadId = "s1", Source = ReplySource.Subagent("s1"),
            Item = new TaskListItem { Id = "1", Description = "Read", Status = "completed" },
        });
        Assert.AreEqual(1, _chatState.State.TaskLists["s1"].CompletedCount);

        await _frontend.OnActiveWorkSnapshotAsync(new ActiveStatusResponse { Subagents = [], IsProcessing = false, Work = [] });
        Assert.AreEqual(0, _chatState.State.TaskLists.Count, "a snapshot with nothing running drops the plan");
    }

    // ── The turn observer ────────────────────────────────────────────────

    [TestMethod]
    public void TurnObserver_AppliesProgressAndHeartbeats_ToTheTurn()
    {
        _chatState.Apply(new TurnStarted("turn-1"));
        var observer = new ChatTurnObserver(_chatState, "turn-1");

        observer.Report(Reply(ReplySource.Primary, isFinal: false) with
        {
            ToolCall = new ToolCallInfo { ToolCallId = "c1", ToolCallName = "web_search", Phase = ToolCallPhase.Started },
        });
        observer.Report(Reply(ReplySource.Primary, isFinal: false));
        observer.OnRunHeartbeat(new RunHeartbeat
        {
            ThreadId = "s1", RunId = "turn-1", Timestamp = DateTimeOffset.UtcNow, ElapsedSeconds = 15, Status = "Calling web_search",
        });

        var log = _chatState.State.Messages.Single();
        Assert.AreEqual(MessageCategory.PrimaryProgress, log.Category);
        Assert.AreEqual("c1", log.ActivityLogEntries[0].ToolCall?.ToolCallId);
        Assert.AreEqual(2, log.ActivityLogEntries.Count);
        Assert.AreEqual("Calling web_search", _chatState.State.ThinkingMessage);
    }

    [TestMethod]
    [DataRow(TurnResultOutcome.Success, TurnOutcome.Completed)]
    [DataRow(TurnResultOutcome.TimedOut, TurnOutcome.TimedOut)]
    [DataRow(TurnResultOutcome.AgentLost, TurnOutcome.AgentLost)]
    [DataRow(TurnResultOutcome.Cancelled, TurnOutcome.Cancelled)]
    [DataRow(TurnResultOutcome.Error, TurnOutcome.AgentError)]
    public void TurnResult_MapsToTurnEnded(TurnResultOutcome outcome, TurnOutcome expected)
    {
        var ended = ChatTurnObserver.ToTurnEnded("turn-1",
            new TurnResult { Outcome = outcome, CorrelationId = "turn-1", Error = "e" });

        Assert.AreEqual(expected, ended.Outcome);
        Assert.AreEqual("turn-1", ended.TurnId);
    }
}
