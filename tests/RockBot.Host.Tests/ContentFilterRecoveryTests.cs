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
    public void StripConversationHistory_DropsToolResultsOrphanedByAssistantRemoval()
    {
        // The previous assistant turn invoked a tool — when that assistant message is
        // stripped as "history", the tool-result message it paired with is left orphaned.
        // The orphan would re-trigger HTTP 400 on the recovery retry, so it must go too.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "what's the weather?"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "get_weather")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "72F sunny")]),
            new(ChatRole.Assistant, "It's 72 and sunny."),
            new(ChatRole.User, "current request"),
        };

        var removed = AgentLoopRunner.StripConversationHistory(messages);

        Assert.AreEqual(3, removed, "Two assistant messages and one user history message removed");
        Assert.AreEqual(2, messages.Count, "Orphan tool result should be swept too, leaving system + current user");
        Assert.AreEqual(ChatRole.System, messages[0].Role);
        Assert.AreEqual("current request", messages[1].Text);
    }

    [TestMethod]
    public void StripConversationHistory_KeepsToolMessageWithPlainText()
    {
        // Tool messages that don't contain FunctionResultContent (e.g. plain text contents)
        // shouldn't be touched by the orphan sweep — they're not part of a tool_call pairing.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "old"),
            new(ChatRole.Tool, "free-form tool note"),
            new(ChatRole.User, "current"),
        };

        var removed = AgentLoopRunner.StripConversationHistory(messages);

        Assert.AreEqual(1, removed);
        Assert.AreEqual(3, messages.Count);
        Assert.AreEqual(ChatRole.Tool, messages[1].Role);
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
