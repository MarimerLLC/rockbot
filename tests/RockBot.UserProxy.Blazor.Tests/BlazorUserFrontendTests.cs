using RockBot.UserProxy;
using RockBot.UserProxy.Blazor.Services;
using RockBot.UserProxy.ChatState;

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

    // ── history reload (#809) ────────────────────────────────────────────

    [TestMethod]
    public void HistoryEvent_GivesUnsolicitedTurnsTheAnchorTheyHadLive()
    {
        // The anchor is drawn when the page renders, from the message's origin, in the viewer's zone.
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var now = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        var history = new ConversationHistoryResponse
        {
            Turns =
            [
                new ConversationHistoryTurn { Role = "user", Content = "hello", Timestamp = now.AddMinutes(-30), SessionId = "blazor-session" },
                new ConversationHistoryTurn
                {
                    Role = "assistant", Content = "Nothing needs your attention.", Timestamp = now.AddMinutes(-5),
                    SessionId = "scheduled", Source = ReplySource.Scheduled,
                    Origin = new ReplyOrigin("scheduled", "daily-summary", now.AddMinutes(-6), "scheduled"),
                },
                new ConversationHistoryTurn
                {
                    // Started here, in this session: the anchor is suppressed, as it is live.
                    Role = "assistant", Content = "Vendor B.", Timestamp = now.AddMinutes(-1), SessionId = "blazor-session",
                    Origin = new ReplyOrigin("blazor", "research vendors", now.AddMinutes(-20), "blazor-session"),
                },
            ],
        };

        var loaded = BlazorUserFrontend.HistoryEvent(history, "blazor-session", now);
        _chatState.Apply(loaded);
        var shown = _chatState.State.Messages
            .Select(m => MessageDisplay.Content(m, "blazor-session", now, chicago)).ToList();

        Assert.AreEqual("blazor-session", loaded.SessionId);
        Assert.AreEqual("hello", shown[0]);
        Assert.AreEqual(
            "> ↳ Re: \"daily-summary\"\n> started 03:54 from scheduled · 6m ago\n\nNothing needs your attention.",
            shown[1]);
        Assert.AreEqual("Vendor B.", shown[2]);
    }

    [TestMethod]
    public async Task LiveReply_AnchorDrawnInTheViewersZone_NotBakedIntoTheContent()
    {
        // The server runs in UTC; a viewer in Chicago must not see the UTC start time.
        var startedAt = DateTimeOffset.Parse("2026-11-16T15:55:11Z");
        await _frontend.DisplayReplyAsync(new AgentReply
        {
            Content = "High-tier routing was 8.3%.",
            SessionId = "scheduled",
            AgentName = "RockBot",
            IsFinal = true,
            Source = ReplySource.Scheduled,
            Origin = new ReplyOrigin("scheduled", "llm-high-tier-cost-guard", startedAt, "scheduled"),
        });

        var msg = GetLastMessage();
        Assert.AreEqual("High-tier routing was 8.3%.", msg.Content);
        Assert.AreEqual(
            "> ↳ Re: \"llm-high-tier-cost-guard\"\n> started 09:55 from scheduled · 2h ago\n\nHigh-tier routing was 8.3%.",
            MessageDisplay.Content(msg, "blazor-session", startedAt.AddHours(2),
                TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")));
    }

    [TestMethod]
    public async Task SavedCopy_KeepsTheAnchor_DatedInsteadOfAgo()
    {
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        await _frontend.DisplayReplyAsync(new AgentReply
        {
            Content = "High-tier routing was 8.3%.",
            SessionId = "scheduled",
            AgentName = "RockBot",
            IsFinal = true,
            Source = ReplySource.Scheduled,
            Origin = new ReplyOrigin("scheduled", "llm-high-tier-cost-guard", DateTimeOffset.Parse("2026-11-16T15:55:11Z"), "scheduled"),
        });
        await _frontend.DisplayReplyAsync(new AgentReply
        {
            // Started here: no anchor live, none saved.
            Content = "Vendor B.",
            SessionId = "blazor-session",
            AgentName = "RockBot",
            IsFinal = true,
            Origin = new ReplyOrigin("blazor", "research vendors", DateTimeOffset.Parse("2026-11-16T15:00:00Z"), "blazor-session"),
        });

        var messages = _chatState.State.Messages;
        Assert.AreEqual(
            "> ↳ Re: \"llm-high-tier-cost-guard\"\n> started Nov 16 09:55 from scheduled\n\nHigh-tier routing was 8.3%.",
            MessageDisplay.ForSaving(messages[^2], "blazor-session", chicago));
        Assert.AreEqual("Vendor B.", MessageDisplay.ForSaving(messages[^1], "blazor-session", chicago));
    }

    [TestMethod]
    public void MessageTime_DatedUnlessFromTodayInTheViewersZone()
    {
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var now = new DateTime(2026, 11, 17, 2, 24, 0, DateTimeKind.Utc); // 11-16 20:24 in Chicago

        // 15:55 UTC on 11-16 is earlier the same day in Chicago; on 11-15 it's yesterday there.
        Assert.AreEqual("09:55:11", MessageDisplay.Time(new DateTime(2026, 11, 16, 15, 55, 11, DateTimeKind.Utc), now, chicago));
        Assert.AreEqual("Nov 15, 09:55:11", MessageDisplay.Time(new DateTime(2026, 11, 15, 15, 55, 11, DateTimeKind.Utc), now, chicago));
        Assert.AreEqual("Dec 31, 2025, 18:00:00", MessageDisplay.Time(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), now, chicago));
    }

    [TestMethod]
    public void HistoryEvent_ImportedCopyWithoutOrigin_NamesTheSessionItCameFrom()
    {
        // #841: the copy is stored in blazor-session, but the anchor is for where it came from.
        var now = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        var history = new ConversationHistoryResponse
        {
            Turns =
            [
                new ConversationHistoryTurn
                {
                    Role = "assistant", Content = "The result.", Timestamp = now, SessionId = "blazor-session",
                    Source = ReplySource.Subagent("t1"), ImportedFromSessionId = "cli-session",
                },
            ],
        };

        var loaded = BlazorUserFrontend.HistoryEvent(history, "blazor-session", now);

        Assert.AreEqual(
            "> ↳ From a subagent (task t1) in another conversation\n> session \"cli-session\"\n\nThe result.",
            loaded.Turns[0].Content);
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
        _chatState.Apply(new AgentInfoReceived("RockBot", "1.0.0"));
        var turn = new ConversationHistoryTurn
        {
            Role = "user",
            Content = "[Subagent task abc completed]: Found 3 events.",
            Timestamp = DateTimeOffset.UtcNow,
            AgentName = "subagent-abc123"
        };

        _chatState.Apply(new HistoryLoaded([turn], "blazor-session"));

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.SubagentActivity, msg.Category);
    }

    [TestMethod]
    public void LoadHistory_WorkerTurn_CategorizedAsSubagentActivity()
    {
        // Worker tool-invocation history turns (when persisted) also carry a
        // worker-{taskId} AgentName. They must categorize the same as subagent
        // activity so they don't show up as inbound A2A in restored chats.
        _chatState.Apply(new AgentInfoReceived("RockBot", "1.0.0"));
        var turn = new ConversationHistoryTurn
        {
            Role = "user",
            Content = "[Worker abc123 progress]: calling get_calendar_events",
            Timestamp = DateTimeOffset.UtcNow,
            AgentName = "worker-abc123"
        };

        _chatState.Apply(new HistoryLoaded([turn], "blazor-session"));

        var msg = GetLastMessage();
        Assert.AreEqual(MessageCategory.SubagentActivity, msg.Category);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private ChatMessage GetLastMessage()
    {
        Assert.IsTrue(_chatState.State.Messages.Count > 0, "Expected at least one message in chat state");
        return _chatState.State.Messages[^1];
    }
}
