using static RockBot.UserProxy.ChatState.Tests.ChatEventFixtures;

namespace RockBot.UserProxy.ChatState.Tests;

/// <summary>What Simple and Detailed draw (#828).</summary>
[TestClass]
public class ChatVisibilityTests
{
    private static AgentReply SubagentResult(string taskId, string content) =>
        Reply(content) with { Source = ReplySource.Subagent(taskId) };

    private static TaskListItem Item(string id, string description) =>
        new() { Id = id, Description = description, Status = TaskListStatus.Pending };

    /// <summary>A turn that spawns a subagent, with all the kinds of output around it.</summary>
    private static ChatViewState FullTurn() => ChatReducer.Replay(
    [
        new UserMessageSent("Research BBR vs CUBIC", "user", Session) { Timestamp = At(0), MessageId = "ask" },
        new TurnStarted("turn-1") { Timestamp = At(0) },
        new TurnProgress("turn-1", "Planning") { Timestamp = At(1), MessageId = "primary-log" },
        new SubagentRunStarted(new SubagentStarted
        {
            SubagentRunId = "abc", Kind = ReplySourceKind.Subagent, Description = "research", ThreadId = Session,
            StartedAt = At(2),
        }) { Timestamp = At(2), MessageId = "sub-log" },
        new TurnEnded("turn-1", TurnOutcome.Completed) { Reply = Reply("I've started a subagent on that."), Timestamp = At(3), MessageId = "ack" },
        new ReplyReceived(Reply("routing…", isFinal: false), MessageCategory.PrimaryFinal) { Timestamp = At(4), MessageId = "interim" },
        new ReplyReceived(SubagentResult("abc", "BBR wins on lossy links."), MessageCategory.SubagentActivity) { Timestamp = At(10), MessageId = "sub-result" },
        new ReplyReceived(Reply("Short version: BBR on lossy links, CUBIC otherwise."), MessageCategory.PrimaryFinal) { Timestamp = At(12), MessageId = "synthesis" },
        new ReplyReceived(Reply("Inbound call handled", agentName: "PeerAgent", sessionId: "a2a-inbound"), MessageCategory.A2AActivity) { Timestamp = At(13), MessageId = "a2a" },
        new ReplyReceived(Reply("Dream pass done", sessionId: "scheduled-system"), MessageCategory.ScheduledSystem) { Timestamp = At(14), MessageId = "dream" },
        new ReplyReceived(Reply("Daily brief: nothing urgent.", sessionId: "scheduled"), MessageCategory.ScheduledUser) { Timestamp = At(15), MessageId = "brief" },
        new ErrorReported("Could not save response") { Timestamp = At(16), MessageId = "error" },
    ]);

    private static string[] Ids(IEnumerable<ChatMessage> messages) => messages.Select(m => m.MessageId).ToArray();

    [TestMethod]
    public void Simple_ShowsWhatIsMeantForThePerson()
    {
        var visible = ChatVisibility.VisibleMessages(FullTurn(), ChatDetailLevel.Simple, At(20));

        CollectionAssert.AreEqual(new[] { "ask", "ack", "synthesis", "brief", "error" }, Ids(visible));
    }

    [TestMethod]
    public void Detailed_ShowsEverything()
    {
        var state = FullTurn();

        var visible = ChatVisibility.VisibleMessages(state, ChatDetailLevel.Detailed, At(20));

        CollectionAssert.AreEqual(Ids(state.Messages), Ids(visible));
        CollectionAssert.IsSubsetOf(new[] { "primary-log", "sub-log", "interim", "sub-result", "a2a", "dream" }, Ids(visible));
    }

    [TestMethod]
    public void Simple_ShowsASubagentResultNoReplyFollowed_OnceTheGraceHasPassed()
    {
        var state = ChatReducer.Replay(
        [
            new UserMessageSent("Research it", "user", Session) { Timestamp = At(0), MessageId = "ask" },
            new ReplyReceived(SubagentResult("abc", "Findings"), MessageCategory.SubagentActivity) { Timestamp = At(10), MessageId = "sub-result" },
        ]);

        var soon = ChatVisibility.VisibleMessages(state, ChatDetailLevel.Simple, At(10) + TimeSpan.FromMinutes(1));
        var later = ChatVisibility.VisibleMessages(state, ChatDetailLevel.Simple, At(10) + ChatVisibility.UnansweredResultGrace);

        CollectionAssert.AreEqual(new[] { "ask" }, Ids(soon), "RockBot may still be writing its reply");
        CollectionAssert.AreEqual(new[] { "ask", "sub-result" }, Ids(later));
    }

    [TestMethod]
    public void Simple_ShowsASubagentResultNoReplyFollowed_OnceThePersonMovesOn()
    {
        var state = ChatReducer.Replay(
        [
            new ReplyReceived(SubagentResult("abc", "Findings"), MessageCategory.SubagentActivity) { Timestamp = At(10), MessageId = "sub-result" },
            new UserMessageSent("Anything else?", "user", Session) { Timestamp = At(11), MessageId = "next" },
        ]);

        var visible = ChatVisibility.VisibleMessages(state, ChatDetailLevel.Simple, At(12));

        CollectionAssert.AreEqual(new[] { "sub-result", "next" }, Ids(visible));
    }

    [TestMethod]
    public void Simple_KeepsAnAnsweredSubagentResultHidden_EvenAfterTheGrace()
    {
        var visible = ChatVisibility.VisibleMessages(FullTurn(), ChatDetailLevel.Simple, At(10) + TimeSpan.FromHours(1));

        CollectionAssert.DoesNotContain(Ids(visible), "sub-result");
    }

    [TestMethod]
    public void TheTurnsOwnReply_DoesNotAnswerAResultThatBeatIt()
    {
        // A fast subagent finishes before RockBot's "I've started…" reply to the turn.
        var state = ChatReducer.Replay(
        [
            new UserMessageSent("Research it", "user", Session) { Timestamp = At(0), MessageId = "ask" },
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new ReplyReceived(SubagentResult("abc", "Findings"), MessageCategory.SubagentActivity) { Timestamp = At(3), MessageId = "sub-result" },
            new TurnEnded("turn-1", TurnOutcome.Completed) { Reply = Reply("I've started a subagent."), Timestamp = At(4), MessageId = "ack" },
        ]);

        var visible = ChatVisibility.VisibleMessages(state, ChatDetailLevel.Simple, At(3) + ChatVisibility.UnansweredResultGrace);

        CollectionAssert.AreEqual(new[] { "ask", "sub-result", "ack" }, Ids(visible));
    }

    [TestMethod]
    public void NextVisibilityChange_IsWhenTheSoonestGraceRunsOut()
    {
        var state = ChatReducer.Replay(
        [
            new ReplyReceived(SubagentResult("abc", "First"), MessageCategory.SubagentActivity) { Timestamp = At(10), MessageId = "r1" },
            new ReplyReceived(SubagentResult("def", "Second"), MessageCategory.SubagentActivity) { Timestamp = At(20), MessageId = "r2" },
        ]);

        Assert.AreEqual(At(10) + ChatVisibility.UnansweredResultGrace, ChatVisibility.NextVisibilityChange(state, ChatDetailLevel.Simple, At(30)));
        Assert.AreEqual(At(20) + ChatVisibility.UnansweredResultGrace,
            ChatVisibility.NextVisibilityChange(state, ChatDetailLevel.Simple, At(10) + ChatVisibility.UnansweredResultGrace),
            "the first is showing by then");
        Assert.IsNull(ChatVisibility.NextVisibilityChange(state, ChatDetailLevel.Simple, At(20) + ChatVisibility.UnansweredResultGrace));
        Assert.IsNull(ChatVisibility.NextVisibilityChange(state, ChatDetailLevel.Detailed, At(30)), "Detailed draws them already");
        Assert.IsNull(ChatVisibility.NextVisibilityChange(FullTurn(), ChatDetailLevel.Simple, At(20)), "an answered result never changes");
    }

    [TestMethod]
    public void AnErrorDoesNotCountAsRockBotsReply()
    {
        var state = ChatReducer.Replay(
        [
            new ReplyReceived(SubagentResult("abc", "Findings"), MessageCategory.SubagentActivity) { Timestamp = At(10), MessageId = "sub-result" },
            new ErrorReported("Lost contact with the agent") { Timestamp = At(11), MessageId = "error" },
        ]);

        var visible = ChatVisibility.VisibleMessages(state, ChatDetailLevel.Simple, At(10) + ChatVisibility.UnansweredResultGrace);

        CollectionAssert.AreEqual(new[] { "sub-result", "error" }, Ids(visible));
    }

    [TestMethod]
    public void VisibleTaskLists_SimpleShowsOnlyThePrimaryPlan()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TaskListSnapshotReceived(new TaskListSnapshot
            {
                ThreadId = Session, Source = ReplySource.Subagent("abc"), Items = [Item("a", "Read docs")],
            }) { Timestamp = At(1) },
            new TaskListSnapshotReceived(new TaskListSnapshot
            {
                ThreadId = Session, RunId = "turn-1", Source = ReplySource.Primary, Items = [Item("1", "Search")],
            }) { Timestamp = At(2) },
        ]);

        var simple = ChatVisibility.VisibleTaskLists(state, ChatDetailLevel.Simple);
        var detailed = ChatVisibility.VisibleTaskLists(state, ChatDetailLevel.Detailed);

        CollectionAssert.AreEqual(new[] { ReplySourceKind.Primary }, simple.Select(l => l.Key).ToArray());
        CollectionAssert.AreEqual(new[] { ReplySourceKind.Primary, "abc" }, detailed.Select(l => l.Key).ToArray(), "the primary plan first");
    }
}
