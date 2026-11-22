namespace RockBot.A2A.Tests;

[TestClass]
public class LateA2ANotificationHandlerTests
{
    private static LateA2ANotificationMessage Message(string? instructions = null) => new(
        PrimarySessionId: "session/c1",
        SubagentTaskId: "abc",
        SubagentName: "subagent-abc",
        PeerAgent: LateA2ANotificationMessage.McpPeerPrefix + "scheduler",
        Kind: NotificationKind.Result,
        WorkingMemoryKey: "session/c1/notifications/mcp/w_1/result",
        Instructions: instructions);

    [TestMethod]
    public void TheTurn_PointsToTheWorkingMemoryPayload()
    {
        var turn = LateA2ANotificationHandler.SyntheticTurn(Message());

        StringAssert.StartsWith(turn, "A late result arrived from MCP server 'scheduler' (a background task) for your completed subagent 'subagent-abc'.");
        StringAssert.Contains(turn, "working memory at 'session/c1/notifications/mcp/w_1/result'");
        StringAssert.EndsWith(turn, "so the context is not opaque.");
    }

    [TestMethod]
    public void TheSendersInstructions_FollowTheTurn()
    {
        const string instructions = "[rockbot] The change is to x.\n\nThe user's instructions for this watch:\n> Finalize it.";

        var turn = LateA2ANotificationHandler.SyntheticTurn(Message(instructions));

        StringAssert.EndsWith(turn, "so the context is not opaque.\n\n" + instructions);
    }

    [TestMethod]
    public void BlankInstructions_AddNothing()
    {
        Assert.AreEqual(
            LateA2ANotificationHandler.SyntheticTurn(Message()),
            LateA2ANotificationHandler.SyntheticTurn(Message("  ")));
    }
}
