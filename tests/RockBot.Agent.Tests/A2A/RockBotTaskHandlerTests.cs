using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;

namespace RockBot.Agent.A2A.Tests;

[TestClass]
public class RockBotTaskHandlerTests
{
    private StubWorkingMemory _workingMemory = null!;
    private StubTrustStore _trustStore = null!;
    private StubNotificationQueue _notificationQueue = null!;
    private StubUserActivityMonitor _activityMonitor = null!;
    private StubSessionTracker _sessionTracker = null!;

    [TestInitialize]
    public void Setup()
    {
        _workingMemory = new StubWorkingMemory();
        _trustStore = new StubTrustStore();
        _notificationQueue = new StubNotificationQueue();
        _activityMonitor = new StubUserActivityMonitor();
        _sessionTracker = new StubSessionTracker();
    }

    // ── query-availability skill ─────────────────────────────────────────

    [TestMethod]
    public async Task QueryAvailability_Busy_WhenActiveLoop()
    {
        _sessionTracker.ActiveLoop = true;
        _activityMonitor.IsActive = false;
        var handler = CreateHandler();
        SetupActLevelTrust("CallerBot", ["query-availability"]);

        var request = A2ATestEnvelopeHelper.CreateRequest(skill: "query-availability");
        var context = CreateVerifiedContext(request, "CallerBot");

        var result = await handler.HandleTaskAsync(request, context);

        Assert.AreEqual(AgentTaskState.Completed, result.State);
        var text = ExtractText(result);
        Assert.AreEqual("busy", text);
    }

    [TestMethod]
    public async Task QueryAvailability_Available_WhenRecentlyActive()
    {
        _sessionTracker.ActiveLoop = false;
        _activityMonitor.IsActive = true;
        var handler = CreateHandler();
        SetupActLevelTrust("CallerBot", ["query-availability"]);

        var request = A2ATestEnvelopeHelper.CreateRequest(skill: "query-availability");
        var context = CreateVerifiedContext(request, "CallerBot");

        var result = await handler.HandleTaskAsync(request, context);

        var text = ExtractText(result);
        Assert.AreEqual("available, may be delayed", text);
    }

    [TestMethod]
    public async Task QueryAvailability_Away_WhenIdle()
    {
        _sessionTracker.ActiveLoop = false;
        _activityMonitor.IsActive = false;
        var handler = CreateHandler();
        SetupActLevelTrust("CallerBot", ["query-availability"]);

        var request = A2ATestEnvelopeHelper.CreateRequest(skill: "query-availability");
        var context = CreateVerifiedContext(request, "CallerBot");

        var result = await handler.HandleTaskAsync(request, context);

        var text = ExtractText(result);
        Assert.AreEqual("away", text);
    }

    // ── notify-user skill ────────────────────────────────────────────────

    [TestMethod]
    public async Task NotifyUser_WritesToWorkingMemoryAndEnqueuesNotification()
    {
        var handler = CreateHandler();
        SetupActLevelTrust("CallerBot", ["notify-user"]);

        var request = A2ATestEnvelopeHelper.CreateRequest(
            skill: "notify-user", message: "Meeting moved to 3pm");
        var context = CreateVerifiedContext(request, "CallerBot");

        var result = await handler.HandleTaskAsync(request, context);

        Assert.AreEqual(AgentTaskState.Completed, result.State);
        Assert.AreEqual("User has been notified.", ExtractText(result));

        // Verify working memory was written
        Assert.IsTrue(_workingMemory.Store.ContainsKey($"a2a-inbox/{request.TaskId}/summary"));
        Assert.IsTrue(_workingMemory.Store.ContainsKey($"a2a-inbox/{request.TaskId}/status"));
        Assert.AreEqual("notification-delivered",
            _workingMemory.Store[$"a2a-inbox/{request.TaskId}/status"]);

        // Verify notification was enqueued
        Assert.AreEqual(1, _notificationQueue.Enqueued.Count);
        var notification = _notificationQueue.Enqueued[0];
        Assert.AreEqual(request.TaskId, notification.TaskId);
        Assert.AreEqual("CallerBot", notification.CallerName);
        Assert.AreEqual("notify-user", notification.SkillId);
        StringAssert.Contains(notification.Summary, "Meeting moved to 3pm");
    }

    // ── trust tracking ───────────────────────────────────────────────────

    [TestMethod]
    public async Task HandleTask_UpdatesTrustInteractionCount()
    {
        var handler = CreateHandler();
        SetupActLevelTrust("CallerBot", ["query-availability"]);

        var request = A2ATestEnvelopeHelper.CreateRequest(skill: "query-availability");
        var context = CreateVerifiedContext(request, "CallerBot");

        await handler.HandleTaskAsync(request, context);

        Assert.AreEqual(1, _trustStore.Updates.Count);
        Assert.AreEqual(11, _trustStore.Updates[0].InteractionCount,
            "Should increment from the existing count (10) by 1");
    }

    [TestMethod]
    public async Task HandleTask_NewCaller_CreatesObserveLevelEntry()
    {
        var handler = CreateHandler();
        // Don't pre-configure trust — let the store create a new entry

        var request = A2ATestEnvelopeHelper.CreateRequest(skill: "general");
        var context = CreateVerifiedContext(request, "NewAgent");

        // This will go to the Observe path which needs AgentLoopRunner.
        // We verify the trust entry was created at Observe level.
        // The observe path will fail because AgentLoopRunner is null,
        // but trust tracking happens before dispatch.
        try { await handler.HandleTaskAsync(request, context); }
        catch { /* Expected — AgentLoopRunner is null for this test */ }

        Assert.IsTrue(_trustStore.Entries.ContainsKey("NewAgent"));
        Assert.AreEqual(AgentTrustLevel.Observe, _trustStore.Entries["NewAgent"].Level);
    }

    // ── trust-based routing ──────────────────────────────────────────────

    [TestMethod]
    public async Task HandleTask_ActLevelWithUnapprovedSkill_FallsToObserve()
    {
        var handler = CreateHandler();
        // Act level but only "notify-user" is approved — not "custom-skill"
        SetupActLevelTrust("CallerBot", ["notify-user"]);

        var request = A2ATestEnvelopeHelper.CreateRequest(skill: "custom-skill");
        var context = CreateVerifiedContext(request, "CallerBot");

        // Should fall through to Observe path since "custom-skill" isn't approved.
        // AgentLoopRunner is null, so this will throw.
        await Assert.ThrowsExactlyAsync<NullReferenceException>(
            () => handler.HandleTaskAsync(request, context));
    }

    [TestMethod]
    public async Task HandleTask_ActLevelWithUnknownBuiltinSkill_FallsToObserve()
    {
        var handler = CreateHandler();
        // Approved for "unknown-builtin" at Act level
        SetupActLevelTrust("CallerBot", ["unknown-builtin"]);

        var request = A2ATestEnvelopeHelper.CreateRequest(skill: "unknown-builtin");
        var context = CreateVerifiedContext(request, "CallerBot");

        // The switch default case falls to HandleObserveAsync
        await Assert.ThrowsExactlyAsync<NullReferenceException>(
            () => handler.HandleTaskAsync(request, context));
    }

    // ── fallback identity ────────────────────────────────────────────────

    [TestMethod]
    public async Task HandleTask_NoVerifiedIdentity_UsesFallbackFromSource()
    {
        var handler = CreateHandler();
        SetupActLevelTrust("SourceAgent", ["query-availability"]);

        var request = A2ATestEnvelopeHelper.CreateRequest(skill: "query-availability");
        // Create context WITHOUT verified identity — handler should use fallback
        var envelope = A2ATestEnvelopeHelper.CreateTaskEnvelope(request, source: "SourceAgent");
        var context = A2ATestEnvelopeHelper.CreateContext(envelope, identity: null);

        var result = await handler.HandleTaskAsync(request, context);

        // Should still work — falls back to Source field
        Assert.AreEqual(AgentTaskState.Completed, result.State);
    }

    // ── observe path (full LLM integration) ──────────────────────────────

    [TestMethod]
    public async Task HandleTask_ObserveLevel_RunsLlmAndWritesToMemory()
    {
        var llmClient = new StubLlmClient { ResponseText = "Summary: external agent wants a meeting" };
        var handler = CreateHandlerWithLlm(llmClient);
        // Default trust is Observe — no need to set up Act level

        var request = A2ATestEnvelopeHelper.CreateRequest(
            skill: "general", message: "Can we schedule a meeting?");
        var context = CreateVerifiedContext(request, "ExternalAgent");

        var result = await handler.HandleTaskAsync(request, context);

        Assert.AreEqual(AgentTaskState.Completed, result.State);

        // Verify working memory was populated
        Assert.IsTrue(_workingMemory.Store.ContainsKey($"a2a-inbox/{request.TaskId}/caller"));
        Assert.IsTrue(_workingMemory.Store.ContainsKey($"a2a-inbox/{request.TaskId}/request"));
        Assert.AreEqual("pending-review",
            _workingMemory.Store[$"a2a-inbox/{request.TaskId}/status"]);

        // Verify notification was enqueued
        Assert.AreEqual(1, _notificationQueue.Enqueued.Count);
        Assert.AreEqual("ExternalAgent", _notificationQueue.Enqueued[0].CallerName);
    }

    // ── contextId-based continuation ────────────────────────────────────

    [TestMethod]
    public async Task HandleTask_WithContextId_UsesContextIdBasedSessionId()
    {
        var llmClient = new StubLlmClient { ResponseText = "Follow-up summary" };
        var handler = CreateHandlerWithLlm(llmClient);

        // First request — no contextId
        var request1 = new AgentTaskRequest
        {
            TaskId = Guid.NewGuid().ToString("N"),
            Skill = "general",
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = "Can we meet?" }]
            }
        };
        var context1 = CreateVerifiedContext(request1, "CallerBot");
        await handler.HandleTaskAsync(request1, context1);

        // Second request — with contextId (simulating a follow-up)
        var contextId = Guid.NewGuid().ToString("N");
        var request2 = new AgentTaskRequest
        {
            TaskId = Guid.NewGuid().ToString("N"),
            ContextId = contextId,
            Skill = "general",
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = "How about 3pm?" }]
            }
        };
        var context2 = CreateVerifiedContext(request2, "CallerBot");
        var result2 = await handler.HandleTaskAsync(request2, context2);

        Assert.AreEqual(AgentTaskState.Completed, result2.State);
        Assert.AreEqual(contextId, result2.ContextId);
    }

    [TestMethod]
    public async Task HandleTask_WithoutContextId_StartsNewConversation()
    {
        var llmClient = new StubLlmClient { ResponseText = "New conversation summary" };
        var handler = CreateHandlerWithLlm(llmClient);

        var request = A2ATestEnvelopeHelper.CreateRequest(skill: "general", message: "First request");
        var context = CreateVerifiedContext(request, "CallerBot");

        var result = await handler.HandleTaskAsync(request, context);

        Assert.AreEqual(AgentTaskState.Completed, result.State);
        // Without contextId, the sessionId is taskId-based — no continuation
        Assert.IsNull(result.ContextId);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a handler with null AgentLoopRunner/MemoryTools — suitable for
    /// skill dispatch tests that don't hit the Observe path.
    /// </summary>
    private RockBotTaskHandler CreateHandler() =>
        new(agentLoopRunner: null!,
            workingMemory: _workingMemory,
            memoryTools: null!,
            trustStore: _trustStore,
            notificationQueue: _notificationQueue,
            userActivityMonitor: _activityMonitor,
            sessionTracker: _sessionTracker,
            conversationMemory: new StubConversationMemory(),
            logger: NullLogger<RockBotTaskHandler>.Instance);

    /// <summary>
    /// Creates a handler with a real AgentLoopRunner backed by stubs —
    /// suitable for testing the full Observe path.
    /// </summary>
    private RockBotTaskHandler CreateHandlerWithLlm(StubLlmClient llmClient)
    {
        var config = new ConfigurationBuilder().Build();
        var profileOptions = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), $"rockbot-test-{Guid.NewGuid():N}")
        });

        var clock = new AgentClock(config, profileOptions, NullLogger<AgentClock>.Instance);
        var hostOptions = Options.Create(new AgentHostOptions());

        var runner = new AgentLoopRunner(
            llmClient,
            _workingMemory,
            ModelBehavior.Default,
            new StubFeedbackStore(),
            clock,
            hostOptions,
            new StubSkillStore(),
            Array.Empty<IServiceSearchIndex>(),
            new StubConversationMemory(),
            NullLogger<AgentLoopRunner>.Instance);

        var memoryTools = new MemoryTools(
            new StubLongTermMemory(),
            llmClient,
            profileOptions,
            NullLogger<MemoryTools>.Instance);

        return new RockBotTaskHandler(
            runner,
            _workingMemory,
            memoryTools,
            _trustStore,
            _notificationQueue,
            _activityMonitor,
            _sessionTracker,
            new StubConversationMemory(),
            NullLogger<RockBotTaskHandler>.Instance);
    }

    private void SetupActLevelTrust(string agentId, IReadOnlyList<string> approvedSkills)
    {
        _trustStore.Entries[agentId] = new AgentTrustEntry
        {
            AgentId = agentId,
            Level = AgentTrustLevel.Act,
            ApprovedSkills = approvedSkills,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-1),
            LastInteraction = DateTimeOffset.UtcNow.AddHours(-1),
            InteractionCount = 10
        };
    }

    private AgentTaskContext CreateVerifiedContext(AgentTaskRequest request, string callerId)
    {
        var envelope = A2ATestEnvelopeHelper.CreateTaskEnvelope(request, source: callerId);
        var identity = A2ATestEnvelopeHelper.CreateIdentity(callerId);
        return A2ATestEnvelopeHelper.CreateContext(envelope, identity);
    }

    private static string ExtractText(AgentTaskResult result) =>
        result.Message.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault() ?? "";
}
