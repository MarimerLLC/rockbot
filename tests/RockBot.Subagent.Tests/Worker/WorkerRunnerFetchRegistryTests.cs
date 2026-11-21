using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Subagent.Worker;
using RockBot.Tools;

namespace RockBot.Subagent.Tests.Worker;

/// <summary>
/// #847: a worker runs its own loop on its spawner's fetch registry, so a worker re-running a
/// fetch a wisp already made is told where the wisp's output is.
/// </summary>
[TestClass]
public class WorkerRunnerFetchRegistryTests
{
    [TestMethod]
    public async Task Worker_RepeatingASpawnersWispFetch_IsToldWhereTheOutputIs()
    {
        var model = new ScriptedChatClient(
            new FunctionCallContent("c1", "mail__search_emails",
                new Dictionary<string, object?> { ["query"] = "invoice", ["count"] = 50 }),
            "Filtered. [WORKER_RESULT] facts=1 blocked= patterns=0");
        var runner = CreateRunner(model);

        // What the spawner's run holds after a wisp ran the search (an mcp_invoke_tool route).
        var spawnerRegistry = new RunFetchRegistry();
        spawnerRegistry.Observe("mcp_invoke_tool",
            """{"server_name":"mail","tool_name":"search_emails","arguments":{"query":"invoice","count":50}}""",
            succeeded: true, "by wisp wisp-1234 step search", key: "wisp/wisp-1234/search/output");

        WorkerResult result;
        using (RunFetchRegistryContext.Set(spawnerRegistry))
        {
            result = await runner.RunAsync("task-1",
                new WorkerDefinition { Description = "Keep only invoices from Acme" },
                "session-1", "batch-1", TimeSpan.FromMinutes(1), CancellationToken.None);
        }

        Assert.IsTrue(result.IsSuccess, result.FailureReason);
        var toolResult = model.LastMessages!
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.Result?.ToString() ?? string.Empty)
            .Single();
        StringAssert.Contains(toolResult, "by wisp wisp-1234 step search");
        StringAssert.Contains(toolResult, "`wisp/wisp-1234/search/output`");
    }

    private static IWorkerRunner CreateRunner(IChatClient model)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton<ILlmClient>(new ChatClientLlm(model));
        services.AddSingleton<IWorkingMemory>(new DictionaryWorkingMemory());
        services.AddSingleton<IFeedbackStore>(new NoopFeedbackStore());
        services.AddSingleton<IToolRegistry>(new SingleToolRegistry(
            new ToolRegistration
            {
                Name = "mail__search_emails", Description = "Search email", Source = "mcp:mail",
                DownstreamName = "search_emails",
                ParametersSchema = """{"type":"object","properties":{"query":{"type":"string"},"count":{"type":"integer"}}}"""
            },
            new FixedExecutor("50 results")));
        services.AddSingleton<IMessagePublisher>(new NoopPublisher());
        services.AddSingleton<ILongTermMemory>(new NoopLongTermMemory());
        services.AddSingleton<ISkillStore>(new NoopSkillStore());
        var basePath = Path.Combine(Path.GetTempPath(), "rockbot-worker-fetchreg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(basePath);
        var agentProfileOptions = Options.Create(new AgentProfileOptions { BasePath = basePath });
        services.AddSingleton(agentProfileOptions);
        services.AddSingleton(new AgentClock(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            agentProfileOptions,
            NullLoggerFactory.Instance.CreateLogger<AgentClock>()));
        services.AddSingleton(new AgentIdentity("test-agent"));
        // The loop runs the tool calls itself, so the test needs no function-invoking client.
        services.AddSingleton(new ModelBehavior { UseTextBasedToolCalling = true });
        services.AddSingleton(Options.Create(new AgentHostOptions()));
        services.AddSingleton(Options.Create(new WorkerOptions()));
        services.AddSingleton(Options.Create(new WorkingMemoryOptions()));
        services.AddTransient<AgentLoopRunner>();

        var doc = new AgentProfileDocument("stub", null, [], "");
        var profile = new AgentProfile(doc, doc);
        services.AddSingleton(profile);
        var profileHolder = new ProfileHolder();
        profileHolder.Update(profile);
        services.AddSingleton(profileHolder);
        var nameHolder = new AgentNameHolder();
        services.AddSingleton(nameHolder);
        services.AddSingleton<ISystemPromptBuilder>(new DefaultSystemPromptBuilder(profileHolder, nameHolder, Options.Create(new AgentProfileOptions())));
        services.AddSingleton<IRulesStore>(new NoopRulesStore());
        services.AddSingleton<IConversationMemory>(new NoopConversationMemory());
        services.AddSingleton<InjectedMemoryTracker>();
        services.AddSingleton<SkillIndexTracker>();
        services.AddSingleton<SkillRecallTracker>();
        services.AddTransient<AgentContextBuilder>();
        services.AddTransient<IWorkerRunner, WorkerRunner>();

        return services.BuildServiceProvider().GetRequiredService<IWorkerRunner>();
    }

    /// <summary>First call returns the tool call; later calls return the closing text. Keeps the last request.</summary>
    private sealed class ScriptedChatClient(FunctionCallContent call, string finalText) : IChatClient
    {
        private int _calls;

        public List<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            var message = Interlocked.Increment(ref _calls) == 1
                ? new ChatMessage(ChatRole.Assistant, [call])
                : new ChatMessage(ChatRole.Assistant, finalText);
            return Task.FromResult(new ChatResponse(message));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ChatClientLlm(IChatClient client) : ILlmClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct) =>
            client.GetResponseAsync(messages, options, ct);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options, CancellationToken ct) =>
            client.GetResponseAsync(messages, options, ct);
    }

    private sealed class SingleToolRegistry(ToolRegistration registration, IToolExecutor executor) : IToolRegistry
    {
        public IReadOnlyList<ToolRegistration> GetTools() => [registration];
        public IToolExecutor? GetExecutor(string toolName) => toolName == registration.Name ? executor : null;
        public void Register(ToolRegistration reg, IToolExecutor exec) => throw new NotSupportedException();
        public bool Unregister(string toolName) => false;
    }

    private sealed class FixedExecutor(string content) : IToolExecutor
    {
        public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct) =>
            Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId, ToolName = request.ToolName, Content = content, IsError = false
            });
    }

    private sealed class DictionaryWorkingMemory : IWorkingMemory
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null)
        {
            lock (_store) _store[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key)
        {
            lock (_store) return Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);
        }

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }

    private sealed class NoopPublisher : IMessagePublisher
    {
        public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopFeedbackStore : IFeedbackStore
    {
        public Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
        public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
    }

    private sealed class NoopConversationMemory : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopLongTermMemory : ILongTermMemory
    {
        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryEntry?>(null);
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopSkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
    }

    private sealed class NoopRulesStore : IRulesStore
    {
        public IReadOnlyList<string> Rules => [];
        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddAsync(string rule) => Task.CompletedTask;
        public Task RemoveAsync(string rule) => Task.CompletedTask;
    }
}
