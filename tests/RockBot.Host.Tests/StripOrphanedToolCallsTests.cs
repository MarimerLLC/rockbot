using Microsoft.Extensions.AI;

namespace RockBot.Host.Tests;

[TestClass]
public class StripOrphanedToolCallsTests
{
    private static ChatMessage AssistantWithCalls(params (string callId, string name)[] calls)
    {
        var msg = new ChatMessage(ChatRole.Assistant, []);
        foreach (var (callId, name) in calls)
            msg.Contents.Add(new FunctionCallContent(callId, name));
        return msg;
    }

    private static ChatMessage ToolResult(string callId, string result) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    [TestMethod]
    public void NoOrphans_LeavesMessagesUntouched()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "do thing"),
            AssistantWithCalls(("c1", "search")),
            ToolResult("c1", "found 3"),
            new(ChatRole.Assistant, "done"),
        };
        var initialCount = messages.Count;

        var (calls, results) = RockBotFunctionInvokingChatClient.StripOrphanedToolCalls(messages);

        Assert.AreEqual(0, calls);
        Assert.AreEqual(0, results);
        Assert.AreEqual(initialCount, messages.Count);
    }

    [TestMethod]
    public void OrphanedFunctionCall_RemovedFromAssistantMessage()
    {
        // Reproduces the production failure: assistant emitted a tool_call that the loop
        // ran out of iterations to execute, leaving an orphan FunctionCallContent.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "do thing"),
            AssistantWithCalls(("orphan-1", "search")),
        };

        var (calls, results) = RockBotFunctionInvokingChatClient.StripOrphanedToolCalls(messages);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, results);
        // The assistant message had only the orphan call → message should be dropped.
        Assert.AreEqual(1, messages.Count);
        Assert.AreEqual(ChatRole.User, messages[0].Role);
    }

    [TestMethod]
    public void OrphanedFunctionCall_KeepsOtherContentInSameMessage()
    {
        var assistantMsg = new ChatMessage(ChatRole.Assistant, [
            new TextContent("here is what I'll do"),
            new FunctionCallContent("orphan-1", "search"),
        ]);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "do thing"),
            assistantMsg,
        };

        var (calls, results) = RockBotFunctionInvokingChatClient.StripOrphanedToolCalls(messages);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, results);
        Assert.AreEqual(2, messages.Count);
        Assert.AreEqual(1, messages[1].Contents.Count);
        Assert.IsInstanceOfType<TextContent>(messages[1].Contents[0]);
    }

    [TestMethod]
    public void OrphanedToolResult_RemovedFromToolMessage()
    {
        // Symmetric case: tool result with no preceding assistant FunctionCallContent.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "do thing"),
            ToolResult("ghost-call", "result with no parent"),
        };

        var (calls, results) = RockBotFunctionInvokingChatClient.StripOrphanedToolCalls(messages);

        Assert.AreEqual(0, calls);
        Assert.AreEqual(1, results);
        Assert.AreEqual(1, messages.Count);
    }

    [TestMethod]
    public void MixedPairedAndOrphaned_KeepsPairedRemovesOrphan()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "task"),
            AssistantWithCalls(("c1", "search"), ("c2-orphan", "spawn_wisp")),
            ToolResult("c1", "search results"),
            new(ChatRole.Assistant, "incomplete"),
        };

        var (calls, results) = RockBotFunctionInvokingChatClient.StripOrphanedToolCalls(messages);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, results);
        Assert.AreEqual(4, messages.Count);
        // Assistant tool_calls message should keep the paired call only.
        var remaining = messages[1].Contents.OfType<FunctionCallContent>().ToList();
        Assert.AreEqual(1, remaining.Count);
        Assert.AreEqual("c1", remaining[0].CallId);
    }

    [TestMethod]
    public void EmptyMessageList_NoChange()
    {
        var messages = new List<ChatMessage>();

        var (calls, results) = RockBotFunctionInvokingChatClient.StripOrphanedToolCalls(messages);

        Assert.AreEqual(0, calls);
        Assert.AreEqual(0, results);
        Assert.AreEqual(0, messages.Count);
    }
}
