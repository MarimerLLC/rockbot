using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

/// <summary>
/// The loop's tool-call ledger is the one hook through which the session work registry learns
/// about files a run writes (#665): a <c>file_write</c> made inside <see cref="AgentLoopRunner.RunAsync"/>
/// lands in the conversation's artifacts with no per-tool code.
/// </summary>
[TestClass]
public class AgentLoopRunnerSessionWorkTests
{
    [TestMethod]
    [DataRow(false, DisplayName = "native (function-invoking chat client)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task FileWriteDuringARun_IsRecordedAsAnArtifactOfTheConversation(bool textBased)
    {
        var registry = new SessionWorkRegistry();
        registry.LinkSession("subagent-abc123", "session/cli-deck");
        var model = new ToolThenTextChatClient(
            new FunctionCallContent("call-1", "file_write",
                new Dictionary<string, object?> { ["path"] = "drafts/mcp-v2-deck.md", ["content"] = "# MCP v2" }),
            "Deck written to drafts/mcp-v2-deck.md.");
        var runner = CreateRunner(model, textBased, registry);
        var written = new List<string>();
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((string path, string content) =>
            {
                written.Add(path);
                return $"Wrote {content.Length} chars to {path}";
            }, "file_write")]
        };

        await runner.RunAsync(
            [new(ChatRole.System, "You are a test agent."), new(ChatRole.User, "Write the deck")],
            options, "subagent-abc123",
            enableFollowUp: false, enableCompletionEval: false, cancellationToken: CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "drafts/mcp-v2-deck.md" }, written, "the tool ran");
        var artifact = registry.GetSnapshot("cli-deck").Artifacts.Single();
        Assert.AreEqual("drafts/mcp-v2-deck.md", artifact.Path);
        Assert.AreEqual("subagent-abc123", artifact.LastWriterSessionId);
    }

    private static AgentLoopRunner CreateRunner(IChatClient model, bool textBased, ISessionWorkRegistry registry)
    {
        var profileOpts = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-workreg-loop-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(profileOpts.Value.BasePath);
        var clock = new AgentClock(new ConfigurationBuilder().Build(), profileOpts, NullLogger<AgentClock>.Instance);
        var behavior = new ModelBehavior { UseTextBasedToolCalling = textBased };
        var hostOptions = Options.Create(new AgentHostOptions());
        var workingMemory = new StubWorkingMemory();

        IChatClient client = textBased
            ? model
            : new RockBotFunctionInvokingChatClient(model, null, null, behavior,
                new LlmCostEstimator(
                    Options.Create(new LlmPricingOptions
                    {
                        ConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "p.json")
                    }),
                    NullLogger<LlmCostEstimator>.Instance),
                workingMemory, hostOptions, NullLogger.Instance);

        return new AgentLoopRunner(
            new ChatClientLlm(client),
            workingMemory,
            behavior,
            new StubFeedbackStore(),
            clock,
            hostOptions,
            new StubSkillStore(),
            Array.Empty<IServiceSearchIndex>(),
            new StubConversationMemory(),
            NullLogger<AgentLoopRunner>.Instance,
            sessionWorkRegistry: registry);
    }

    /// <summary>First call returns a tool call; later calls return the closing text.</summary>
    private sealed class ToolThenTextChatClient(FunctionCallContent call, string finalText) : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
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

    private sealed class StubWorkingMemory : IWorkingMemory
    {
        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null) => Task.CompletedTask;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
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
