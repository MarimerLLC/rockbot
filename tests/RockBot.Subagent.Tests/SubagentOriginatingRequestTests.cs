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
    }
}
