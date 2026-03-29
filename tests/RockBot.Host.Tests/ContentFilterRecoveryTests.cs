using Microsoft.Extensions.AI;

namespace RockBot.Host.Tests;

[TestClass]
public class ContentFilterRecoveryTests
{
    // ── StripConversationHistory ─────────────────────────────────────────────

    [TestMethod]
    public void StripConversationHistory_RemovesHistoryKeepsLastUser()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant."),
            new(ChatRole.User, "old user message 1"),
            new(ChatRole.Assistant, "old assistant response 1"),
            new(ChatRole.User, "old user message 2"),
            new(ChatRole.Assistant, "old assistant response 2"),
            new(ChatRole.User, "current request"),
        };

        var removed = AgentLoopRunner.StripConversationHistory(messages);

        Assert.AreEqual(4, removed);
        Assert.AreEqual(2, messages.Count);
        Assert.AreEqual(ChatRole.System, messages[0].Role);
        Assert.AreEqual("current request", messages[1].Text);
        Assert.AreEqual(ChatRole.User, messages[1].Role);
    }

    [TestMethod]
    public void StripConversationHistory_KeepsSystemMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.System, "datetime context"),
            new(ChatRole.System, "reasoning scaffolding"),
            new(ChatRole.User, "history turn"),
            new(ChatRole.Assistant, "history response"),
            new(ChatRole.System, "memory recall"),
            new(ChatRole.User, "current request"),
        };

        var removed = AgentLoopRunner.StripConversationHistory(messages);

        Assert.AreEqual(2, removed);
        Assert.AreEqual(5, messages.Count);
        Assert.IsTrue(messages.All(m => m.Role == ChatRole.System || m.Text == "current request"));
    }

    [TestMethod]
    public void StripConversationHistory_NoHistoryReturnsZero()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "only message"),
        };

        var removed = AgentLoopRunner.StripConversationHistory(messages);

        Assert.AreEqual(0, removed);
        Assert.AreEqual(2, messages.Count);
    }

    [TestMethod]
    public void StripConversationHistory_NoUserMessageReturnsZero()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.Assistant, "orphan assistant message"),
        };

        var removed = AgentLoopRunner.StripConversationHistory(messages);

        Assert.AreEqual(0, removed);
    }

    [TestMethod]
    public void StripConversationHistory_KeepsToolMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "old message"),
            new(ChatRole.Assistant, "old response"),
            new(ChatRole.Tool, "tool result from current interaction"),
            new(ChatRole.User, "current request"),
        };

        var removed = AgentLoopRunner.StripConversationHistory(messages);

        Assert.AreEqual(2, removed);
        Assert.AreEqual(3, messages.Count);
        Assert.AreEqual(ChatRole.System, messages[0].Role);
        Assert.AreEqual(ChatRole.Tool, messages[1].Role);
        Assert.AreEqual("current request", messages[2].Text);
    }

    [TestMethod]
    public void StripConversationHistory_EmptyListReturnsZero()
    {
        var messages = new List<ChatMessage>();

        var removed = AgentLoopRunner.StripConversationHistory(messages);

        Assert.AreEqual(0, removed);
    }

    [TestMethod]
    public void StripConversationHistory_MultipleSystemInterleavedWithHistory()
    {
        // Simulates real context: system messages interspersed with history
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "soul"),
            new(ChatRole.System, "directives"),
            new(ChatRole.System, "datetime"),
            new(ChatRole.User, "hi"),
            new(ChatRole.Assistant, "hello!"),
            new(ChatRole.User, "toxic injection text"),
            new(ChatRole.Assistant, "poisoned response"),
            new(ChatRole.System, "memory recall"),
            new(ChatRole.System, "reasoning scaffolding"),
            new(ChatRole.User, "what's my schedule?"),
        };

        var removed = AgentLoopRunner.StripConversationHistory(messages);

        Assert.AreEqual(4, removed);
        Assert.AreEqual(6, messages.Count);
        // All remaining should be system or the current user request
        foreach (var msg in messages)
        {
            Assert.IsTrue(
                msg.Role == ChatRole.System || msg.Text == "what's my schedule?",
                $"Unexpected message: role={msg.Role}, text={msg.Text}");
        }
    }
}
