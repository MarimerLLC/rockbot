using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.UserProxy;
using RockBot.Memory.Tests;

namespace RockBot.Agent.Tests;

[TestClass]
public class UserFeedbackHandlerTests
{
    private const string SessionId = "test-session";
    private const string MessageId = "msg-001";
    private const string AgentName = "TestBot";

    private StubConversationMemory _conversationMemory = null!;
    private TrackingPublisher _publisher = null!;
    private TrackingFeedbackStore _feedbackStore = null!;
    private StubToolRegistry _toolRegistry = null!;
    private StubWorkSerializer _workSerializer = null!;
    private ConfigurableLlmClient _llmClient = null!;
    private UserFeedbackHandler _handler = null!;

    [TestInitialize]
    public void Setup()
    {
        _conversationMemory = new StubConversationMemory();
        _publisher = new TrackingPublisher();
        _feedbackStore = new TrackingFeedbackStore();
        _toolRegistry = new StubToolRegistry();
        _workSerializer = new StubWorkSerializer();
        _llmClient = new ConfigurableLlmClient("Here is a better answer.");

        var agent = new AgentIdentity(AgentName);
        var modelBehavior = ModelBehavior.Default;

        var agentLoopRunner = new AgentLoopRunner(
            _llmClient,
            new StubWorkingMemory(),
            modelBehavior,
            _feedbackStore,
            NullLogger<AgentLoopRunner>.Instance);

        var config = new ConfigurationBuilder().Build();
        var profileOptions = Options.Create(new AgentProfileOptions { BasePath = Path.GetTempPath() });
        var clock = new AgentClock(config, profileOptions, NullLogger<AgentClock>.Instance);

        var profile = new AgentProfile(
            Soul: new AgentProfileDocument("soul", "Test agent", [], "Test agent"),
            Directives: new AgentProfileDocument("directives", "Be helpful", [], "Be helpful"));

        var agentContextBuilder = new AgentContextBuilder(
            profile,
            agent,
            new StubSystemPromptBuilder(),
            new StubRulesStore(),
            modelBehavior,
            _conversationMemory,
            new StubLongTermMemory(),
            new InjectedMemoryTracker(),
            new StubWorkingMemory(),
            new StubSharedMemory(),
            new StubSkillStore(),
            new SkillIndexTracker(),
            new SkillRecallTracker(),
            clock,
            NullLogger<AgentContextBuilder>.Instance);

        var rulesTools = new RulesTools(
            new StubRulesStore(), clock, NullLogger<RulesTools>.Instance);

        var workingMemory = new StubWorkingMemory();
        var skillStore = new StubSkillStore();
        var memoryTools = new MemoryTools(
            new StubLongTermMemory(), _llmClient, profileOptions, NullLogger<MemoryTools>.Instance);
        var toolGuideTools = new ToolGuideTools(
            [], NullLogger<ToolGuideTools>.Instance);

        _handler = new UserFeedbackHandler(
            _conversationMemory,
            _llmClient,
            _publisher,
            agent,
            _feedbackStore,
            agentLoopRunner,
            agentContextBuilder,
            workingMemory,
            memoryTools,
            skillStore,
            _toolRegistry,
            rulesTools,
            toolGuideTools,
            _workSerializer,
            NullLogger<UserFeedbackHandler>.Instance);
    }

    // ── Positive feedback ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task PositiveFeedback_RecordsFeedbackEntry()
    {
        await _handler.HandleAsync(MakeFeedback(isPositive: true), MakeContext());

        Assert.AreEqual(1, _feedbackStore.Entries.Count);
        Assert.AreEqual(FeedbackSignalType.UserThumbsUp, _feedbackStore.Entries[0].SignalType);
    }

    [TestMethod]
    public async Task PositiveFeedback_AddsReinforcementTurnToConversation()
    {
        await _handler.HandleAsync(MakeFeedback(isPositive: true), MakeContext());

        var turns = await _conversationMemory.GetTurnsAsync(SessionId);
        Assert.AreEqual(1, turns.Count);
        Assert.AreEqual("system", turns[0].Role);
        StringAssert.Contains(turns[0].Content, "helpful");
    }

    [TestMethod]
    public async Task PositiveFeedback_DoesNotPublishReply()
    {
        await _handler.HandleAsync(MakeFeedback(isPositive: true), MakeContext());

        Assert.AreEqual(0, _publisher.Published.Count);
    }

    // ── Negative feedback ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task NegativeFeedback_RecordsFeedbackEntry()
    {
        await SeedConversationHistory();

        await _handler.HandleAsync(MakeFeedback(isPositive: false), MakeContext());

        Assert.IsTrue(_feedbackStore.Entries.Any(e =>
            e.SignalType == FeedbackSignalType.UserThumbsDown));
    }

    [TestMethod]
    public async Task NegativeFeedback_PublishesFreshReply()
    {
        await SeedConversationHistory();

        await _handler.HandleAsync(MakeFeedback(isPositive: false), MakeContext());

        Assert.AreEqual(1, _publisher.Published.Count);

        var (topic, envelope) = _publisher.Published[0];
        Assert.AreEqual(UserProxyTopics.UserResponse, topic);

        var reply = envelope.GetPayload<AgentReply>();
        Assert.IsNotNull(reply);
        Assert.AreEqual("Here is a better answer.", reply.Content);
        Assert.AreEqual(SessionId, reply.SessionId);
        Assert.AreEqual(AgentName, reply.AgentName);
        Assert.IsTrue(reply.IsFinal);
    }

    [TestMethod]
    public async Task NegativeFeedback_RecordsReevaluationInConversationMemory()
    {
        await SeedConversationHistory();

        await _handler.HandleAsync(MakeFeedback(isPositive: false), MakeContext());

        var turns = await _conversationMemory.GetTurnsAsync(SessionId);
        var assistantTurns = turns.Where(t => t.Role == "assistant").ToList();
        Assert.IsTrue(assistantTurns.Any(t => t.Content == "Here is a better answer."));
    }

    [TestMethod]
    public async Task NegativeFeedback_NoUserTurns_DoesNotPublish()
    {
        // No conversation history seeded — no user turns exist

        await _handler.HandleAsync(MakeFeedback(isPositive: false), MakeContext());

        Assert.AreEqual(0, _publisher.Published.Count);
    }

    [TestMethod]
    public async Task NegativeFeedback_SlotUnavailable_DoesNotPublish()
    {
        await SeedConversationHistory();
        _workSerializer.SlotAvailable = false;

        await _handler.HandleAsync(MakeFeedback(isPositive: false), MakeContext());

        Assert.AreEqual(0, _publisher.Published.Count);
    }

    [TestMethod]
    public async Task NegativeFeedback_EmptyLlmResponse_DoesNotPublish()
    {
        await SeedConversationHistory();
        _llmClient.ResponseText = "";

        await _handler.HandleAsync(MakeFeedback(isPositive: false), MakeContext());

        Assert.AreEqual(0, _publisher.Published.Count);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task SeedConversationHistory()
    {
        await _conversationMemory.AddTurnAsync(SessionId,
            new ConversationTurn("user", "What is 2 + 2?", DateTimeOffset.UtcNow.AddMinutes(-2)));
        await _conversationMemory.AddTurnAsync(SessionId,
            new ConversationTurn("assistant", "2 + 2 = 5", DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    private static UserFeedback MakeFeedback(bool isPositive) => new()
    {
        SessionId = SessionId,
        MessageId = MessageId,
        IsPositive = isPositive,
        AgentName = AgentName
    };

    private static MessageHandlerContext MakeContext() => new()
    {
        Envelope = MessageEnvelope.Create(
            messageType: typeof(UserFeedback).FullName!,
            body: ReadOnlyMemory<byte>.Empty,
            source: "test"),
        Agent = new AgentIdentity(AgentName),
        Services = null!,
        CancellationToken = CancellationToken.None
    };
}

// ── Stubs ──────────────────────────────────────────────────────────────────

internal sealed class StubConversationMemory : IConversationMemory
{
    private readonly Dictionary<string, List<ConversationTurn>> _sessions = [];

    public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var list))
        {
            list = [];
            _sessions[sessionId] = list;
        }
        list.Add(turn);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var list))
            return Task.FromResult<IReadOnlyList<ConversationTurn>>(list);
        return Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
    }

    public Task ClearAsync(string sessionId, CancellationToken ct = default)
    {
        _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_sessions.Keys.ToList());
}

internal sealed class TrackingPublisher : IMessagePublisher
{
    public List<(string Topic, MessageEnvelope Envelope)> Published { get; } = [];

    public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken ct = default)
    {
        Published.Add((topic, envelope));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TrackingFeedbackStore : IFeedbackStore
{
    public List<FeedbackEntry> Entries { get; } = [];

    public Task AppendAsync(FeedbackEntry entry, CancellationToken ct = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FeedbackEntry>>(Entries.Where(e => e.SessionId == sessionId).ToList());

    public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FeedbackEntry>>(Entries.Where(e => e.Timestamp >= since).Take(maxResults).ToList());
}

internal sealed class StubToolRegistry : IToolRegistry
{
    public IReadOnlyList<ToolRegistration> GetTools() => [];
    public IToolExecutor? GetExecutor(string toolName) => null;
    public void Register(ToolRegistration registration, IToolExecutor executor) { }
    public bool Unregister(string toolName) => false;
}

internal sealed class StubWorkSerializer : IAgentWorkSerializer
{
    public bool SlotAvailable { get; set; } = true;

    public Task<IAsyncDisposable> AcquireForUserAsync(CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable>(new NoOpSlot());

    public Task<IScheduledTaskSlot?> TryAcquireForScheduledAsync(CancellationToken ct) =>
        Task.FromResult<IScheduledTaskSlot?>(SlotAvailable ? new StubSlot(ct) : null);

    private sealed class NoOpSlot : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSlot(CancellationToken ct) : IScheduledTaskSlot
    {
        public CancellationToken Token => ct;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class ConfigurableLlmClient : ILlmClient
{
    public string ResponseText { get; set; }

    public ConfigurableLlmClient(string responseText) => ResponseText = responseText;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseText)));
}

internal sealed class StubWorkingMemory : IWorkingMemory
{
    public Task SetAsync(string sessionId, string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null) => Task.CompletedTask;
    public Task<string?> GetAsync(string sessionId, string key) => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string sessionId) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(string sessionId, string query, int maxResults = 5) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(string sessionId, MemorySearchCriteria criteria) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    public Task DeleteAsync(string sessionId, string key) => Task.CompletedTask;
    public Task ClearAsync(string sessionId) => Task.CompletedTask;
}

internal sealed class StubSharedMemory : ISharedMemory
{
    public Task SetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null) => Task.CompletedTask;
    public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<SharedMemoryEntry>> ListAsync() =>
        Task.FromResult<IReadOnlyList<SharedMemoryEntry>>([]);
    public Task<IReadOnlyList<SharedMemoryEntry>> SearchAsync(MemorySearchCriteria criteria) =>
        Task.FromResult<IReadOnlyList<SharedMemoryEntry>>([]);
    public Task DeleteAsync(string key) => Task.CompletedTask;
    public Task ClearAsync() => Task.CompletedTask;
}

internal sealed class StubSystemPromptBuilder : ISystemPromptBuilder
{
    public string Build(AgentProfile profile, AgentIdentity identity) => "You are a test agent.";
}

internal sealed class StubRulesStore : IRulesStore
{
    public IReadOnlyList<string> Rules => [];
    public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    public Task AddAsync(string rule) => Task.CompletedTask;
    public Task RemoveAsync(string rule) => Task.CompletedTask;
}

internal sealed class StubSkillStore : ISkillStore
{
    public Task SaveAsync(Skill skill) => Task.CompletedTask;
    public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
    public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
    public Task DeleteAsync(string name) => Task.CompletedTask;
    public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults = 5, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Skill>>([]);
}
