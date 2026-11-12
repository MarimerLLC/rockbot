using RockBot.UserProxy.Blazor.Services;
using RockBot.UserProxy.ChatState;

namespace RockBot.UserProxy.Tests;

/// <summary>
/// Covers the busy-state bookkeeping on the singleton <see cref="ChatStateService"/>.
/// The service outlives any one Blazor circuit, so state set by a circuit that later
/// disappears (page reload, SignalR reconnect) must not strand the UI.
/// </summary>
[TestClass]
public sealed class ChatStateServiceProcessingStateTests
{
    private readonly ChatStateService _sut = new();

    private static AgentReply Reply(string content, bool isFinal = true, string agentName = "rockbot")
        => new()
        {
            Content = content,
            SessionId = "test-session",
            AgentName = agentName,
            IsFinal = isFinal
        };

    private static ActiveStatusResponse Status(bool isProcessing)
        => new() { Subagents = [], IsProcessing = isProcessing };

    [TestMethod]
    public void AddAgentReply_PrimaryFinal_ClearsProcessing()
    {
        _sut.Apply(new TurnStarted("turn"));

        _sut.Apply(new ReplyReceived(Reply("Here you go."), MessageCategory.PrimaryFinal));

        Assert.IsFalse(_sut.IsBusy,
            "The primary turn is over — the busy flag must clear even if the sending circuit is gone");
    }

    [TestMethod]
    public void AddAgentReply_NonPrimaryCategory_LeavesProcessing()
    {
        _sut.Apply(new TurnStarted("turn"));

        _sut.Apply(new ReplyReceived(Reply("Subagent finished.", agentName: "subagent-1"), MessageCategory.SubagentActivity));

        Assert.IsTrue(_sut.IsBusy,
            "A subagent result does not end the primary turn");
    }

    [TestMethod]
    public void AddAgentReply_PrimaryFinal_ClosesProgressLog()
    {
        _sut.Apply(new TurnStarted("turn"));
        _sut.Apply(new TurnProgress("turn", "I'm working on that — I'll follow up shortly."));

        var logBubble = _sut.State.Messages.Single(m => m.IsActivityLog);
        Assert.IsTrue(_sut.State.IsActivityLogOpen(logBubble.MessageId));

        _sut.Apply(new ReplyReceived(Reply("Done."), MessageCategory.PrimaryFinal));

        Assert.IsFalse(_sut.State.IsActivityLogOpen(logBubble.MessageId),
            "The progress log stops spinning once the reply lands");
    }

    [TestMethod]
    public void LoadHistory_ClearsStaleActivityLogState()
    {
        // A turn in flight when the page reloads: progress bubble open, busy flag set.
        _sut.Apply(new TurnStarted("turn"));
        _sut.Apply(new TurnProgress("turn", "I'm working on that — I'll follow up shortly."));
        var staleBubbleId = _sut.State.Messages.Single(m => m.IsActivityLog).MessageId;

        _sut.Apply(new HistoryLoaded(
            [new ConversationHistoryTurn { Role = "user", Content = "Hello", Timestamp = DateTimeOffset.UtcNow }],
            "test-session"));

        Assert.IsFalse(_sut.State.IsActivityLogOpen(staleBubbleId),
            "The bubble was discarded with the transcript — its log entry must go too");
        Assert.IsNull(_sut.State.ThinkingMessage);
        Assert.IsFalse(_sut.State.Messages.Any(m => m.IsActivityLog));
    }

    [TestMethod]
    public void LoadHistory_ThenProgress_StartsAFreshLogBubble()
    {
        _sut.Apply(new TurnStarted("turn"));
        _sut.Apply(new TurnProgress("turn", "first turn progress"));

        _sut.Apply(new HistoryLoaded(
            [new ConversationHistoryTurn { Role = "user", Content = "Hello", Timestamp = DateTimeOffset.UtcNow }],
            "test-session"));
        _sut.Apply(new TurnProgress("turn", "second turn progress"));

        var bubble = _sut.State.Messages.Single(m => m.IsActivityLog);
        Assert.AreEqual(1, bubble.ActivityLogEntries.Count,
            "Entries from the discarded turn must not accumulate on the new bubble");
        Assert.AreEqual("second turn progress", bubble.Content);
        Assert.IsTrue(_sut.State.IsActivityLogOpen(bubble.MessageId));
    }

    [TestMethod]
    public void ReconcileActiveStatus_AgentIdle_ClearsStaleProcessing()
    {
        _sut.Apply(new TurnStarted("turn"));

        _sut.Apply(new ActiveStatusReconciled(Status(isProcessing: false)));

        Assert.IsFalse(_sut.IsBusy);
    }

    [TestMethod]
    public void ReconcileActiveStatus_AgentBusy_RestoresProcessing()
    {
        _sut.Apply(new ActiveStatusReconciled(Status(isProcessing: true)));

        Assert.IsTrue(_sut.IsBusy,
            "A reload mid-turn should show the agent as still working");
    }
}
