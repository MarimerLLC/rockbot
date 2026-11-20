using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;
using RockBot.Tools;

namespace RockBot.Wisp.Tests;

/// <summary>
/// #847: a wisp LLM step runs its own loop, but its fetches count toward the run that spawned the
/// wisp. A step that repeats an earlier Direct step's call is told where that output is.
/// </summary>
[TestClass]
public class WispLlmStepFetchRegistryTests
{
    [TestMethod]
    public async Task LlmStep_RepeatingADirectStepsFetch_IsToldWhereTheOutputIs()
    {
        var model = new ScriptedChatClient(
            new FunctionCallContent("c1", "web_search",
                new Dictionary<string, object?> { ["query"] = "invoices", ["count"] = 5 }),
            "Filtered: 3 invoices.");
        var executor = CreateExecutor(model);

        var definition = new WispDefinition
        {
            Description = "Search, then filter",
            Steps =
            [
                new WispStep
                {
                    Id = "search",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "invoices", "count": 5}""").RootElement,
                },
                new WispStep { Id = "filter", Mode = StepMode.Llm, Prompt = "Keep only invoices from Acme." },
            ]
        };

        // The parent run's registry, as AgentLoopRunner.RunAsync binds it around spawn_wisps.
        using var scope = RunFetchRegistryContext.Set(new RunFetchRegistry());
        var result = await executor.ExecuteAsync(definition, "wisp-1234", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.FailedStep?.Error?.Message);
        var toolResult = model.LastMessages!
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.Result?.ToString() ?? string.Empty)
            .Single();
        StringAssert.Contains(toolResult, "by wisp wisp-1234 step search");
        StringAssert.Contains(toolResult, "`wisp/wisp-1234/search/output`");
    }

    private static WispExecutor CreateExecutor(IChatClient model)
    {
        var registry = new FakeToolRegistry();
        registry.Register(
            new ToolRegistration
            {
                Name = "web_search", Description = "Search", Source = "web",
                ParametersSchema = """{"type":"object","properties":{"query":{"type":"string"},"count":{"type":"integer"}}}"""
            },
            new FakeToolExecutor("3 results"));
        var memory = new FakeWorkingMemory();

        var profileOpts = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-wisp-fetchreg-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(profileOpts.Value.BasePath);
        var clock = new AgentClock(new ConfigurationBuilder().Build(), profileOpts, NullLogger<AgentClock>.Instance);
        var runner = new AgentLoopRunner(
            new ChatClientLlm(model),
            memory,
            new ModelBehavior { UseTextBasedToolCalling = true },
            new StubFeedbackStore(),
            clock,
            Options.Create(new AgentHostOptions()),
            new StubSkillStore(),
            Array.Empty<IServiceSearchIndex>(),
            new StubConversationMemory(),
            NullLogger<AgentLoopRunner>.Instance);

        return new WispExecutor(registry, memory, runner, new WispOptions(), NullLogger<WispExecutor>.Instance);
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

    private sealed class StubFeedbackStore : IFeedbackStore
    {
        public Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
        public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
    }

    private sealed class StubConversationMemory : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubSkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
    }
}
