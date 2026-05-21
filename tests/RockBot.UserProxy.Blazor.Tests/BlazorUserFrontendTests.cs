using RockBot.UserProxy;
using RockBot.UserProxy.Blazor.Services;

namespace RockBot.UserProxy.Blazor.Tests;

[TestClass]
public class BlazorUserFrontendTests
{
    private ChatStateService _chatState = null!;
    private BlazorUserFrontend _frontend = null!;

    [TestInitialize]
    public void Setup()
    {
        _chatState = new ChatStateService();
        _frontend = new BlazorUserFrontend(_chatState);
    }

    // ── session-based categorization ─────────────────────────────────────

    [TestMethod]
    public async Task ScheduledSystemSession_CategorizedAsScheduledSystem()
    {
        var reply = new AgentReply
        {
            Content = "Heartbeat check complete",
            SessionId = "scheduled-system",
            AgentName = "RockBot",
            IsFinal = true
        };

        await _frontend.DisplayReplyAsync(reply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.ScheduledSystem, msg.Category);
    }

    [TestMethod]
    public async Task ScheduledUserSession_CategorizedAsScheduledUser()
    {
        var reply = new AgentReply
        {
            Content = "Reminder: standup in 5 minutes",
            SessionId = "scheduled",
            AgentName = "RockBot",
            IsFinal = true
        };

        await _frontend.DisplayReplyAsync(reply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.ScheduledUser, msg.Category);
    }

    [TestMethod]
    public async Task A2AInboundSession_CategorizedAsA2AActivity()
    {
        var reply = new AgentReply
        {
            Content = "Agent HelperBot reached out",
            SessionId = "a2a-inbound",
            AgentName = "A2A-Inbox",
            IsFinal = true
        };

        await _frontend.DisplayReplyAsync(reply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.A2AActivity, msg.Category);
    }

    [TestMethod]
    public async Task A2AInboundSessionPrefix_CategorizedAsA2AActivity()
    {
        // Non-final reply with a2a-inbound session prefix (e.g., progress updates)
        var reply = new AgentReply
        {
            Content = "Processing inbound task...",
            SessionId = "a2a-inbound/task-123",
            AgentName = "A2A-Inbox",
            IsFinal = false
        };

        await _frontend.DisplayStatusAsync(reply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.A2AActivity, msg.Category);
    }

    // ── agent name-based categorization ──────────────────────────────────

    [TestMethod]
    public async Task SubagentName_CategorizedAsSubagentActivity()
    {
        var reply = new AgentReply
        {
            Content = "Searching the web...",
            SessionId = "blazor-session",
            AgentName = "subagent-research",
            IsFinal = false
        };

        await _frontend.DisplayStatusAsync(reply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.SubagentActivity, msg.Category);
    }

    [TestMethod]
    public async Task WorkerName_CategorizedAsSubagentActivity()
    {
        // Workers emit tool-call progress via ToolProgressNotifier with
        // AgentName = "worker-{taskId}". Before the fix this fell through
        // to A2AActivity because workers were neither "subagent-" prefixed
        // nor equal to the primary agent name.
        var reply = new AgentReply
        {
            Content = "Calling get_calendar_events…",
            SessionId = "blazor-session",
            AgentName = "worker-abc123",
            IsFinal = false
        };

        await _frontend.DisplayStatusAsync(reply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.SubagentActivity, msg.Category);
    }

    [TestMethod]
    public async Task WorkerName_InA2AInboundSession_CategorizedAsSubagentActivity()
    {
        // Workers spawned from inside an A2A inbound task receive an
        // a2a-inbound/* session id. The AgentName check must fire BEFORE
        // the session check so worker progress is still labelled subagent
        // activity, not A2A activity.
        var reply = new AgentReply
        {
            Content = "Calling search_emails…",
            SessionId = "a2a-inbound/task-xyz",
            AgentName = "worker-abc123",
            IsFinal = false
        };

        await _frontend.DisplayStatusAsync(reply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.SubagentActivity, msg.Category);
    }

    [TestMethod]
    public async Task WorkerName_AfterLearningPrimaryName_CategorizedAsSubagentActivity()
    {
        // Once the primary agent name is learned, a non-final reply from a
        // "different" agent normally goes to A2AActivity. Workers must NOT
        // hit that branch — the worker- prefix check fires first.
        await _frontend.DisplayReplyAsync(new AgentReply
        {
            Content = "First response",
            SessionId = "blazor-session",
            AgentName = "RockBot",
            IsFinal = true
        });

        var reply = new AgentReply
        {
            Content = "Calling mcp_invoke_tool…",
            SessionId = "blazor-session",
            AgentName = "worker-abc123",
            IsFinal = false
        };

        await _frontend.DisplayStatusAsync(reply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.SubagentActivity, msg.Category);
    }

    // ── final vs non-final ───────────────────────────────────────────────

    [TestMethod]
    public async Task FinalReply_CategorizedAsPrimaryFinal()
    {
        var reply = new AgentReply
        {
            Content = "Here is your answer.",
            SessionId = "blazor-session",
            AgentName = "RockBot",
            IsFinal = true
        };

        await _frontend.DisplayReplyAsync(reply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.PrimaryFinal, msg.Category);
    }

    [TestMethod]
    public async Task FinalReply_LearnsAgentName()
    {
        var reply = new AgentReply
        {
            Content = "First response",
            SessionId = "blazor-session",
            AgentName = "RockBot",
            IsFinal = true
        };

        await _frontend.DisplayReplyAsync(reply);

        // Now send a non-final reply from a different agent — should be A2A
        var externalReply = new AgentReply
        {
            Content = "External progress",
            SessionId = "blazor-session",
            AgentName = "ExternalBot",
            IsFinal = false
        };

        await _frontend.DisplayStatusAsync(externalReply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.A2AActivity, msg.Category);
    }

    [TestMethod]
    public async Task NonFinalFromSameAgent_CategorizedAsPrimaryProgress()
    {
        // First learn the primary agent name
        await _frontend.DisplayReplyAsync(new AgentReply
        {
            Content = "First response",
            SessionId = "blazor-session",
            AgentName = "RockBot",
            IsFinal = true
        });

        // Non-final from the same agent
        var reply = new AgentReply
        {
            Content = "Thinking...",
            SessionId = "blazor-session",
            AgentName = "RockBot",
            IsFinal = false
        };

        await _frontend.DisplayStatusAsync(reply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.PrimaryProgress, msg.Category);
    }

    [TestMethod]
    public async Task NonFinalBeforeLearningPrimaryName_CategorizedAsPrimaryProgress()
    {
        // Send non-final reply BEFORE any final reply has been seen
        var reply = new AgentReply
        {
            Content = "Working on it...",
            SessionId = "blazor-session",
            AgentName = "RockBot",
            IsFinal = false
        };

        await _frontend.DisplayStatusAsync(reply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.PrimaryProgress, msg.Category);
    }

    [TestMethod]
    public async Task NonFinalFromDifferentAgent_BeforeLearning_NotMislabelledAsA2A()
    {
        // Before the first final reply, even messages from a "different" agent
        // should not be labelled A2A — we haven't learned the primary name yet
        var reply = new AgentReply
        {
            Content = "Progress from unknown",
            SessionId = "blazor-session",
            AgentName = "UnknownBot",
            IsFinal = false
        };

        await _frontend.DisplayStatusAsync(reply);

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.PrimaryProgress, msg.Category);
    }

    // ── history categorization (LoadHistory → CategorizeHistoryTurn) ─────

    [TestMethod]
    public void LoadHistory_SubagentTurn_CategorizedAsSubagentActivity()
    {
        _chatState.SetAgentInfo("RockBot", "1.0.0");
        var turn = new ConversationHistoryTurn
        {
            Role = "user",
            Content = "[Subagent task abc completed]: Found 3 events.",
            Timestamp = DateTimeOffset.UtcNow,
            AgentName = "subagent-abc123"
        };

        _chatState.LoadHistory([turn], "blazor-session");

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.SubagentActivity, msg.Category);
    }

    [TestMethod]
    public void LoadHistory_WorkerTurn_CategorizedAsSubagentActivity()
    {
        // Worker tool-invocation history turns (when persisted) also carry a
        // worker-{taskId} AgentName. They must categorize the same as subagent
        // activity so they don't show up as inbound A2A in restored chats.
        _chatState.SetAgentInfo("RockBot", "1.0.0");
        var turn = new ConversationHistoryTurn
        {
            Role = "user",
            Content = "[Worker abc123 progress]: calling get_calendar_events",
            Timestamp = DateTimeOffset.UtcNow,
            AgentName = "worker-abc123"
        };

        _chatState.LoadHistory([turn], "blazor-session");

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.SubagentActivity, msg.Category);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private ChatMessage GetLastMessage()
    {
        Assert.IsTrue(_chatState.Messages.Count > 0, "Expected at least one message in chat state");
        return _chatState.Messages[^1];
    }
}
