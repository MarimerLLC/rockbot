using System.Text;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Subagent.Tests;

/// <summary>
/// The user request behind a subagent travels with its result to the synthesis turn (#666), so
/// the primary's relay is checked against what the user asked rather than the subagent's report.
/// </summary>
[TestClass]
public class SubagentOriginatingRequestTests
{
    private static SubagentResultMessage Result(string taskId, string? request) => new()
    {
        TaskId = taskId,
        SubagentSessionId = $"subagent-{taskId}",
        PrimarySessionId = "session/s1",
        Output = "deck built: 30 slides",
        IsSuccess = true,
        Timestamp = DateTimeOffset.UtcNow,
        BatchId = "batch-1",
        OriginatingUserRequest = request,
    };

    [TestMethod]
    public void Combine_SingleResult_ReturnsItsRequest()
    {
        Assert.AreEqual(
            "figure out a way to update the doc",
            SubagentResultHandler.CombineOriginatingRequests([Result("a", "figure out a way to update the doc")]));
    }

    [TestMethod]
    public void Combine_SiblingsFromOneTurn_Collapse()
    {
        var combined = SubagentResultHandler.CombineOriginatingRequests(
        [
            Result("a", "research both talks"),
            Result("b", "research both talks"),
        ]);

        Assert.AreEqual("research both talks", combined);
    }

    [TestMethod]
    public void Combine_DistinctRequests_AreAllListed()
    {
        var combined = SubagentResultHandler.CombineOriginatingRequests(
        [
            Result("a", "trim the deck"),
            Result("b", null),
            Result("c", "fix the speaker notes"),
        ])!;

        StringAssert.Contains(combined, "(1) trim the deck");
        StringAssert.Contains(combined, "(2) fix the speaker notes");
    }

    [TestMethod]
    public void Combine_NoRequests_ReturnsNull()
    {
        Assert.IsNull(SubagentResultHandler.CombineOriginatingRequests([Result("a", null), Result("b", "  ")]));
    }

    [TestMethod]
    public void ResultMessage_RoundTripsTheRequest()
    {
        var envelope = Result("a", "trim the deck to about 11 slides").ToEnvelope<SubagentResultMessage>(source: "subagent-a");
        var back = envelope.GetPayload<SubagentResultMessage>()!;

        Assert.AreEqual("trim the deck to about 11 slides", back.OriginatingUserRequest);
    }

    // ── Later user messages (#715) ───────────────────────────────────────────────

    private static readonly DateTimeOffset Spawn = new(2026, 10, 13, 16, 41, 0, TimeSpan.Zero);

    private static SubagentResultMessage Spawned(string taskId, string? request, DateTimeOffset? at) =>
        Result(taskId, request) with { SpawnedAt = at };

    private static ConversationTurn UserTurn(string content, DateTimeOffset at, string? agentName = null) =>
        new("user", content, at) { AgentName = agentName };

    [TestMethod]
    public void LaterUserMessages_TakesTheUsersOwnTurnsAfterTheSpawn()
    {
        const string original = "What's on my work calendar tomorrow?";
        var turns = new List<ConversationTurn>
        {
            UserTurn("earlier chat", Spawn.AddMinutes(-5)),
            UserTurn(original, Spawn.AddSeconds(-2)),
            new("assistant", "Checking your work calendar…", Spawn.AddSeconds(1)),
            UserTurn("no, I meant my personal calendar, not work", Spawn.AddSeconds(20)),
            UserTurn("[Subagent task a completed]: …", Spawn.AddSeconds(30), agentName: "subagent-a"),
            UserTurn("[A2A result]", Spawn.AddSeconds(31), agentName: "peer-agent"),
        };

        var later = SubagentResultHandler.LaterUserMessages(turns, [Spawned("a", original, Spawn)]);

        CollectionAssert.AreEqual(new[] { "no, I meant my personal calendar, not work" }, later.ToArray());
    }

    [TestMethod]
    public void LaterUserMessages_KeepsARepeatOfTheOriginalThatUndoesACorrection()
    {
        // work → personal → work: the repeat is the user's latest word and must not be dropped.
        const string work = "What's on my work calendar tomorrow?";
        var turns = new List<ConversationTurn>
        {
            UserTurn(work, Spawn.AddSeconds(-1)),
            UserTurn("personal calendar instead", Spawn.AddSeconds(10)),
            UserTurn(work, Spawn.AddSeconds(20)),
        };

        var later = SubagentResultHandler.LaterUserMessages(turns, [Spawned("a", work, Spawn)]);

        CollectionAssert.AreEqual(new[] { "personal calendar instead", work }, later.ToArray());
    }

    [TestMethod]
    public void ComposedRequest_FromALongOriginal_KeepsTheCorrectionThroughTheSpawnCap()
    {
        // A follow-on spawn records the composed request; the spawn cap keeps its start. The
        // correction predates that subagent's own spawn, so this record is the only place it lives.
        var original = "List my work calendar events. " + new string('x', 5000);
        const string correction = "no, I meant my personal calendar, not work";

        var composed = CompletionEvalTriggers.WithLaterUserMessages(original, [correction])!;
        var recorded = SubagentManager.CapOriginatingRequest(composed)!;

        Assert.AreEqual(composed, recorded, "the composed request must fit the spawn cap untouched");
        Assert.IsTrue(composed.Length <= CompletionEvalTriggers.MaxComposedRequestChars);
        StringAssert.StartsWith(composed, "List my work calendar events.");
        StringAssert.EndsWith(composed, "- " + correction);
    }

    [TestMethod]
    public void LaterUserMessages_UsesTheEarliestSpawnInTheBatch_AndKeepsTheLastThree()
    {
        var turns = Enumerable.Range(1, 5)
            .Select(i => UserTurn($"m{i}", Spawn.AddSeconds(i * 10)))
            .ToList();

        var later = SubagentResultHandler.LaterUserMessages(turns,
        [
            Spawned("a", "x", Spawn.AddSeconds(25)),
            Spawned("b", "x", Spawn.AddSeconds(5)),
        ]);

        CollectionAssert.AreEqual(new[] { "m3", "m4", "m5" }, later.ToArray());
    }

    [TestMethod]
    public void LaterUserMessages_NoSpawnTime_ReturnsNone()
    {
        var turns = new List<ConversationTurn> { UserTurn("no, personal", Spawn.AddSeconds(5)) };

        Assert.AreEqual(0, SubagentResultHandler.LaterUserMessages(turns, [Spawned("a", "work calendar", null)]).Count);
    }

    [TestMethod]
    public void ResultMessage_RoundTripsTheSpawnTime()
    {
        var envelope = Spawned("a", "x", Spawn).ToEnvelope<SubagentResultMessage>(source: "subagent-a");

        Assert.AreEqual(Spawn, envelope.GetPayload<SubagentResultMessage>()!.SpawnedAt);
    }

    [TestMethod]
    public void ResultMessage_FromAnOlderBuild_DeserializesWithNullRequest()
    {
        // A result published before #666 has no originatingUserRequest field.
        const string json =
            "{\"taskId\":\"a\",\"subagentSessionId\":\"subagent-a\",\"primarySessionId\":\"session/s1\"," +
            "\"output\":\"done\",\"isSuccess\":true,\"timestamp\":\"2026-10-09T12:00:00Z\"}";
        var envelope = MessageEnvelope.Create(
            messageType: typeof(SubagentResultMessage).FullName!,
            body: Encoding.UTF8.GetBytes(json),
            source: "subagent-a");

        var back = envelope.GetPayload<SubagentResultMessage>()!;
        Assert.AreEqual("a", back.TaskId);
        Assert.IsNull(back.OriginatingUserRequest);
        Assert.IsNull(back.SpawnedAt);
    }
}
