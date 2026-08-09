using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers <see cref="AgentHostOptions.MaxLlmContextTurns"/>, which controls how many of the
/// most recent conversation turns <see cref="AgentContextBuilder"/> replays into the LLM
/// context. It was a hard-coded 20 before; large-context conversational agents need to raise
/// it without a rebuild.
/// </summary>
[TestClass]
public class AgentContextBuilderContextTurnsTests
{
    [TestMethod]
    public void DefaultIs20()
    {
        Assert.AreEqual(20, new AgentHostOptions().MaxLlmContextTurns,
            "Default must stay 20 so existing deployments are unaffected.");
    }

    [TestMethod]
    public async Task DefaultOptions_ReplaysAtMost20Turns()
    {
        var builder = BuildBuilder(new FixedHistoryConversationMemory(turnCount: 60));

        var messages = await builder.BuildAsync("session-default", "current message", CancellationToken.None);

        Assert.AreEqual(20, CountHistoryTurns(messages),
            "With default options only the last 20 turns should reach the model.");
    }

    [TestMethod]
    public async Task RaisedOption_ReplaysThatManyTurns()
    {
        var builder = BuildBuilder(
            new FixedHistoryConversationMemory(turnCount: 60),
            new AgentHostOptions { MaxLlmContextTurns = 50 });

        var messages = await builder.BuildAsync("session-50", "current message", CancellationToken.None);

        Assert.AreEqual(50, CountHistoryTurns(messages),
            "MaxLlmContextTurns=50 should replay the last 50 turns.");
    }

    [TestMethod]
    public async Task RaisedOption_KeepsTheMostRecentTurns()
    {
        var builder = BuildBuilder(
            new FixedHistoryConversationMemory(turnCount: 60),
            new AgentHostOptions { MaxLlmContextTurns = 50 });

        var messages = await builder.BuildAsync("session-recency", "current message", CancellationToken.None);
        var replayed = HistoryTurns(messages).ToList();

        // Turns are numbered 0..59; the last 50 are 10..59.
        Assert.AreEqual("turn-10", replayed[0], "Oldest replayed turn should be turn-10.");
        Assert.AreEqual("turn-59", replayed[^1], "Newest replayed turn should be turn-59.");
    }

    [TestMethod]
    public async Task HistoryShorterThanWindow_ReplaysEverything()
    {
        var builder = BuildBuilder(
            new FixedHistoryConversationMemory(turnCount: 7),
            new AgentHostOptions { MaxLlmContextTurns = 50 });

        var messages = await builder.BuildAsync("session-short", "current message", CancellationToken.None);

        Assert.AreEqual(7, CountHistoryTurns(messages),
            "A window larger than the available history must not pad or throw.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Synthetic history turns are the only messages whose text starts with "turn-".</summary>
    private static IEnumerable<string> HistoryTurns(IReadOnlyList<ChatMessage> messages) =>
        messages.Select(m => m.Text).Where(t => t.StartsWith("turn-", StringComparison.Ordinal));

    private static int CountHistoryTurns(IReadOnlyList<ChatMessage> messages) => HistoryTurns(messages).Count();

    private static AgentContextBuilder BuildBuilder(
        IConversationMemory conversationMemory,
        AgentHostOptions? hostOptions = null)
    {
        var profileHolder = new ProfileHolder();
        var doc = new AgentProfileDocument("soul", null, [], "I am a test agent.");
        profileHolder.Update(new AgentProfile(doc, doc));
        var nameHolder = new AgentNameHolder();

        var agentProfileOptions = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-ctxturns-test-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(agentProfileOptions.Value.BasePath);

        var clock = new AgentClock(
            new ConfigurationBuilder().Build(),
            agentProfileOptions,
            NullLoggerFactory.Instance.CreateLogger<AgentClock>());

        return new AgentContextBuilder(
            profileHolder: profileHolder,
            agent: new AgentIdentity("TestBot"),
            promptBuilder: new DefaultSystemPromptBuilder(profileHolder, nameHolder, Options.Create(new AgentProfileOptions())),
            rulesStore: new StubRulesStore(),
            modelBehavior: ModelBehavior.Default,
            conversationMemory: conversationMemory,
            longTermMemory: new StubLongTermMemory(),
            injectedMemoryTracker: new InjectedMemoryTracker(),
            workingMemory: new StubWorkingMemory(),
            skillStore: new StubSkillStore(),
            skillIndexTracker: new SkillIndexTracker(),
            skillRecallTracker: new SkillRecallTracker(),
            clock: clock,
            serviceSearchIndexProviders: [],
            knowledgeGraphProviders: [],
            knowledgeGraphOptions: Options.Create(new KnowledgeGraphOptions()),
            embeddingGenerators: [],
            logger: NullLogger<AgentContextBuilder>.Instance,
            agentHostOptions: hostOptions is null ? null : Options.Create(hostOptions));
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    /// <summary>Returns <paramref name="turnCount"/> alternating turns named <c>turn-{i}</c>.</summary>
    private sealed class FixedHistoryConversationMemory(int turnCount) : IConversationMemory
    {
        private readonly IReadOnlyList<ConversationTurn> _turns =
            [.. Enumerable.Range(0, turnCount).Select(i =>
                new ConversationTurn(i % 2 == 0 ? "user" : "assistant", $"turn-{i}", DateTimeOffset.UtcNow))];

        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_turns);

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubLongTermMemory : ILongTermMemory
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

    private sealed class StubRulesStore : IRulesStore
    {
        public IReadOnlyList<string> Rules => [];
        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddAsync(string rule) => Task.CompletedTask;
        public Task RemoveAsync(string rule) => Task.CompletedTask;
    }
}
