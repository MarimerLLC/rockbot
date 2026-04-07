using Microsoft.Extensions.AI;

namespace RockBot.Llm.Copilot.Tests;

[TestClass]
public class MessageFormatterTests
{
    [TestMethod]
    public void SingleUserMessage_PassedDirectly()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello world")
        };

        var (system, user) = MessageFormatter.Format(messages);

        Assert.AreEqual(string.Empty, system);
        Assert.AreEqual("Hello world", user);
    }

    [TestMethod]
    public void SystemMessage_ExtractedToSystemPrompt()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are helpful."),
            new(ChatRole.User, "Hi")
        };

        var (system, user) = MessageFormatter.Format(messages);

        Assert.AreEqual("You are helpful.", system);
        Assert.AreEqual("Hi", user);
    }

    [TestMethod]
    public void MultipleSystemMessages_Concatenated()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System part 1"),
            new(ChatRole.System, "System part 2"),
            new(ChatRole.User, "Hi")
        };

        var (system, user) = MessageFormatter.Format(messages);

        Assert.AreEqual("System part 1\n\nSystem part 2", system);
        Assert.AreEqual("Hi", user);
    }

    [TestMethod]
    public void MultiTurnConversation_FormatsWithHistoryTags()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What is 2+2?"),
            new(ChatRole.Assistant, "4"),
            new(ChatRole.User, "And 3+3?")
        };

        var (_, user) = MessageFormatter.Format(messages);

        Assert.IsTrue(user.Contains("<conversation_history>"));
        Assert.IsTrue(user.Contains("[user]: What is 2+2?"));
        Assert.IsTrue(user.Contains("[assistant]: 4"));
        Assert.IsTrue(user.Contains("</conversation_history>"));
        Assert.IsTrue(user.TrimEnd().EndsWith("And 3+3?"));
    }

    [TestMethod]
    public void ToolCallAndResult_FormattedCorrectly()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Check calendar"),
            new(ChatRole.Assistant, [new FunctionCallContent("call1", "get_events", new Dictionary<string, object?> { ["date"] = "today" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call1", "{\"events\": []}")]),
            new(ChatRole.User, "What about tomorrow?")
        };

        var (_, user) = MessageFormatter.Format(messages);

        Assert.IsTrue(user.Contains("[tool_call]: get_events("));
        Assert.IsTrue(user.Contains("[tool_result]:"));
        Assert.IsTrue(user.TrimEnd().EndsWith("What about tomorrow?"));
    }

    [TestMethod]
    public void EmptyConversation_ReturnsEmpty()
    {
        var messages = new List<ChatMessage>();

        var (system, user) = MessageFormatter.Format(messages);

        Assert.AreEqual(string.Empty, system);
        Assert.AreEqual(string.Empty, user);
    }

    [TestMethod]
    public void OnlySystemMessages_EmptyUserPrompt()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System only")
        };

        var (system, user) = MessageFormatter.Format(messages);

        Assert.AreEqual("System only", system);
        Assert.AreEqual(string.Empty, user);
    }

    [TestMethod]
    public void TwoUserMessages_FormatsWithHistory()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "First"),
            new(ChatRole.User, "Second")
        };

        var (_, user) = MessageFormatter.Format(messages);

        Assert.IsTrue(user.Contains("<conversation_history>"));
        Assert.IsTrue(user.Contains("[user]: First"));
        Assert.IsTrue(user.TrimEnd().EndsWith("Second"));
    }
}
