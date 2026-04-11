using Microsoft.Extensions.AI;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Agent.A2A.Tests;

internal sealed class StubWorkingMemory : IWorkingMemory
{
    public Dictionary<string, string> Store { get; } = new();

    public Task SetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null)
    {
        Store[key] = value;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key) =>
        Task.FromResult(Store.TryGetValue(key, out var v) ? v : null);

    public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

    public Task DeleteAsync(string key)
    {
        Store.Remove(key);
        return Task.CompletedTask;
    }

    public Task ClearAsync(string? prefix = null)
    {
        Store.Clear();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
}

internal sealed class StubTrustStore : IAgentTrustStore
{
    public Dictionary<string, AgentTrustEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AgentTrustEntry> Updates { get; } = [];

    public Task<AgentTrustEntry> GetOrCreateAsync(string agentId, CancellationToken ct)
    {
        if (Entries.TryGetValue(agentId, out var entry))
            return Task.FromResult(entry);

        entry = new AgentTrustEntry
        {
            AgentId = agentId,
            Level = AgentTrustLevel.Observe,
            FirstSeen = DateTimeOffset.UtcNow,
            LastInteraction = DateTimeOffset.UtcNow,
            InteractionCount = 0
        };
        Entries[agentId] = entry;
        return Task.FromResult(entry);
    }

    public Task UpdateAsync(AgentTrustEntry entry, CancellationToken ct)
    {
        Entries[entry.AgentId] = entry;
        Updates.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentTrustEntry>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AgentTrustEntry>>(Entries.Values.ToList());
}

internal sealed class StubNotificationQueue : IInboundNotificationQueue
{
    public List<InboundNotification> Enqueued { get; } = [];
    public int PendingCount => Enqueued.Count;

    public Task EnqueueAsync(InboundNotification notification, CancellationToken ct)
    {
        Enqueued.Add(notification);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InboundNotification>> DrainAsync(CancellationToken ct)
    {
        var items = Enqueued.ToList();
        Enqueued.Clear();
        return Task.FromResult<IReadOnlyList<InboundNotification>>(items);
    }
}

internal sealed class StubUserActivityMonitor : IUserActivityMonitor
{
    public bool IsActive { get; set; }
    public void RecordActivity() { }
    public bool IsUserActive(TimeSpan idleThreshold) => IsActive;
}

internal sealed class StubSessionTracker : ISessionTracker
{
    public bool ActiveLoop { get; set; }
    public SessionHandle BeginSession(string sessionId, CancellationToken hostCt) => new(CancellationToken.None, 0);
    public void EndSession(string sessionId, long generation) { }
    public bool HasActiveUserLoop(string sessionId) => ActiveLoop;
}

internal sealed class StubLlmClient : ILlmClient
{
    public string ResponseText { get; set; } = "LLM summary response";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseText)));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ModelTier tier,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseText)));
}

internal sealed class StubFeedbackStore : IFeedbackStore
{
    public Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
    public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
}

internal sealed class StubSkillStore : ISkillStore
{
    public Task SaveAsync(Skill skill) => Task.CompletedTask;
    public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
    public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
    public Task DeleteAsync(string name) => Task.CompletedTask;
    public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
        Task.FromResult<IReadOnlyList<Skill>>([]);
}

internal sealed class StubConversationMemory : IConversationMemory
{
    public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
    public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

internal sealed class StubLongTermMemory : ILongTermMemory
{
    public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
    public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<MemoryEntry?>(null);
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

internal static class A2ATestEnvelopeHelper
{
    public static MessageEnvelope CreateTaskEnvelope(
        AgentTaskRequest request,
        string source = "TestCaller")
    {
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        return MessageEnvelope.Create(
            messageType: typeof(AgentTaskRequest).FullName!,
            body: body,
            source: source);
    }

    public static AgentTaskContext CreateContext(
        MessageEnvelope envelope,
        VerifiedAgentIdentity? identity = null)
    {
        var handlerContext = new MessageHandlerContext
        {
            Envelope = envelope,
            Agent = new AgentIdentity("RockBot"),
            Services = null!,
            CancellationToken = CancellationToken.None
        };

        if (identity is not null)
            handlerContext.Items[VerifiedAgentIdentity.ContextKey] = identity;

        return new AgentTaskContext
        {
            MessageContext = handlerContext,
            PublishStatus = (_, _) => Task.CompletedTask
        };
    }

    public static VerifiedAgentIdentity CreateIdentity(string agentId, bool selfAsserted = true) =>
        new()
        {
            AgentId = agentId,
            DisplayName = agentId,
            Issuer = selfAsserted ? "self" : "registry",
            IsSelfAsserted = selfAsserted
        };

    public static AgentTaskRequest CreateRequest(
        string skill = "general",
        string message = "Hello from external agent",
        string? taskId = null) =>
        new()
        {
            TaskId = taskId ?? Guid.NewGuid().ToString("N"),
            Skill = skill,
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = message }]
            }
        };
}
