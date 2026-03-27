using RockBot.UserProxy.Blazor.Services;

namespace RockBot.UserProxy.Tests;

[TestClass]
public sealed class ChatStateServiceLoadHistoryTests
{
    private readonly ChatStateService _sut = new();

    [TestInitialize]
    public void Setup()
    {
        // Mirrors real startup: agent info is loaded before history
        _sut.SetAgentInfo("rockbot", "1.0.0");
    }

    [TestMethod]
    public void LoadHistory_UserTurn_CategorizedAsUserInput()
    {
        var turns = new List<ConversationHistoryTurn>
        {
            new() { Role = "user", Content = "Hello", Timestamp = DateTimeOffset.UtcNow }
        };

        _sut.LoadHistory(turns, "test-session");

        var msg = _sut.Messages.Single();
        Assert.IsTrue(msg.IsFromUser);
        Assert.AreEqual(MessageCategory.UserInput, msg.Category);
        Assert.IsTrue(msg.IsExpanded);
    }

    [TestMethod]
    public void LoadHistory_AssistantTurn_CategorizedAsPrimaryFinal()
    {
        var turns = new List<ConversationHistoryTurn>
        {
            new() { Role = "assistant", Content = "Hi there!", Timestamp = DateTimeOffset.UtcNow, AgentName = "rockbot" }
        };

        _sut.LoadHistory(turns, "test-session");

        var msg = _sut.Messages.Single();
        Assert.IsFalse(msg.IsFromUser);
        Assert.AreEqual(MessageCategory.PrimaryFinal, msg.Category);
        Assert.IsTrue(msg.IsExpanded);
    }

    [TestMethod]
    public void LoadHistory_SubagentTurn_CategorizedAsSubagentActivity()
    {
        var turns = new List<ConversationHistoryTurn>
        {
            new() { Role = "user", Content = "[Subagent task abc completed]: results here", Timestamp = DateTimeOffset.UtcNow, AgentName = "subagent-abc" }
        };

        _sut.LoadHistory(turns, "test-session");

        var msg = _sut.Messages.Single();
        Assert.IsFalse(msg.IsFromUser);
        Assert.AreEqual(MessageCategory.SubagentActivity, msg.Category);
        Assert.IsFalse(msg.IsExpanded, "Subagent bubbles should be collapsed by default");
    }

    [TestMethod]
    public void LoadHistory_A2ATurn_CategorizedAsA2AActivity()
    {
        var turns = new List<ConversationHistoryTurn>
        {
            new() { Role = "assistant", Content = "Primary reply", Timestamp = DateTimeOffset.UtcNow, AgentName = "rockbot" },
            new() { Role = "user", Content = "[Agent 'weather-bot' completed task 1]", Timestamp = DateTimeOffset.UtcNow, AgentName = "weather-bot" }
        };

        _sut.LoadHistory(turns, "test-session");

        var a2aMsg = _sut.Messages[1];
        Assert.IsFalse(a2aMsg.IsFromUser);
        Assert.AreEqual(MessageCategory.A2AActivity, a2aMsg.Category);
        Assert.IsFalse(a2aMsg.IsExpanded, "A2A bubbles should be collapsed by default");
    }

    [TestMethod]
    public void LoadHistory_SystemTurn_Filtered()
    {
        var turns = new List<ConversationHistoryTurn>
        {
            new() { Role = "user", Content = "Hello", Timestamp = DateTimeOffset.UtcNow },
            new() { Role = "system", Content = "[All 3 subagent tasks completed. Synthesize the results.]", Timestamp = DateTimeOffset.UtcNow },
            new() { Role = "assistant", Content = "Here are the results.", Timestamp = DateTimeOffset.UtcNow, AgentName = "rockbot" }
        };

        _sut.LoadHistory(turns, "test-session");

        Assert.AreEqual(2, _sut.Messages.Count, "System turn should be filtered out");
        Assert.AreEqual("Hello", _sut.Messages[0].Content);
        Assert.AreEqual("Here are the results.", _sut.Messages[1].Content);
    }

    [TestMethod]
    public void LoadHistory_BackwardCompatible_NullAgentName()
    {
        // Simulates turns from old persisted JSON files without AgentName
        var turns = new List<ConversationHistoryTurn>
        {
            new() { Role = "user", Content = "Hello", Timestamp = DateTimeOffset.UtcNow },
            new() { Role = "assistant", Content = "Hi!", Timestamp = DateTimeOffset.UtcNow }
        };

        _sut.LoadHistory(turns, "test-session");

        Assert.AreEqual(2, _sut.Messages.Count);
        Assert.AreEqual(MessageCategory.UserInput, _sut.Messages[0].Category);
        Assert.IsTrue(_sut.Messages[0].IsFromUser);
        Assert.AreEqual(MessageCategory.PrimaryFinal, _sut.Messages[1].Category);
        Assert.IsFalse(_sut.Messages[1].IsFromUser);
    }

    [TestMethod]
    public void LoadHistory_PreservesAgentName()
    {
        var turns = new List<ConversationHistoryTurn>
        {
            new() { Role = "user", Content = "[Subagent task x completed]: output", Timestamp = DateTimeOffset.UtcNow, AgentName = "subagent-x" },
            new() { Role = "assistant", Content = "Summary", Timestamp = DateTimeOffset.UtcNow, AgentName = "rockbot" }
        };

        _sut.LoadHistory(turns, "test-session");

        Assert.AreEqual("subagent-x", _sut.Messages[0].AgentName);
        Assert.AreEqual("rockbot", _sut.Messages[1].AgentName);
    }

    [TestMethod]
    public void LoadHistory_ClearsExistingMessages()
    {
        // Load initial history
        _sut.LoadHistory(new List<ConversationHistoryTurn>
        {
            new() { Role = "user", Content = "First", Timestamp = DateTimeOffset.UtcNow }
        }, "session-1");

        Assert.AreEqual(1, _sut.Messages.Count);

        // Load new history — should replace, not append
        _sut.LoadHistory(new List<ConversationHistoryTurn>
        {
            new() { Role = "user", Content = "Second", Timestamp = DateTimeOffset.UtcNow },
            new() { Role = "assistant", Content = "Reply", Timestamp = DateTimeOffset.UtcNow }
        }, "session-2");

        Assert.AreEqual(2, _sut.Messages.Count);
        Assert.AreEqual("Second", _sut.Messages[0].Content);
    }

    [TestMethod]
    public void LoadHistory_MixedConversation_CorrectCategories()
    {
        var turns = new List<ConversationHistoryTurn>
        {
            new() { Role = "user", Content = "Research climate change", Timestamp = DateTimeOffset.UtcNow },
            new() { Role = "user", Content = "[Subagent task 1 completed]: Temperature data", Timestamp = DateTimeOffset.UtcNow, AgentName = "subagent-1" },
            new() { Role = "user", Content = "[Subagent task 2 completed]: Sea level data", Timestamp = DateTimeOffset.UtcNow, AgentName = "subagent-2" },
            new() { Role = "assistant", Content = "Here is a synthesis of the research.", Timestamp = DateTimeOffset.UtcNow, AgentName = "rockbot" },
            new() { Role = "user", Content = "Also check with weather-bot", Timestamp = DateTimeOffset.UtcNow },
            new() { Role = "user", Content = "[Agent 'weather-bot' completed task 3]", Timestamp = DateTimeOffset.UtcNow, AgentName = "weather-bot" },
            new() { Role = "assistant", Content = "Weather-bot confirmed the findings.", Timestamp = DateTimeOffset.UtcNow, AgentName = "rockbot" },
        };

        _sut.LoadHistory(turns, "test-session");

        Assert.AreEqual(7, _sut.Messages.Count);
        Assert.AreEqual(MessageCategory.UserInput, _sut.Messages[0].Category);
        Assert.AreEqual(MessageCategory.SubagentActivity, _sut.Messages[1].Category);
        Assert.AreEqual(MessageCategory.SubagentActivity, _sut.Messages[2].Category);
        Assert.AreEqual(MessageCategory.PrimaryFinal, _sut.Messages[3].Category);
        Assert.AreEqual(MessageCategory.UserInput, _sut.Messages[4].Category);
        Assert.AreEqual(MessageCategory.A2AActivity, _sut.Messages[5].Category);
        Assert.AreEqual(MessageCategory.PrimaryFinal, _sut.Messages[6].Category);
    }
}
