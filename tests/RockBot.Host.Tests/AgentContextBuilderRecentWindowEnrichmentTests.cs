using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers the recent-window query enrichment in <see cref="AgentContextBuilder"/>
/// added under issue #397. A fact-introducing follow-up on a live thread carries too
/// few distinctive terms to retrieve what the conversation is actually about, so the
/// per-turn search query is anchored by appending the last couple of turns. Enrichment
/// applies only on an established thread — first turns, stale threads, and short
/// messages keep the existing behaviour.
/// </summary>
[TestClass]
public class AgentContextBuilderRecentWindowEnrichmentTests
{
    // The 66-char production reproducer from #397.
    private const string Reproducer = "Hopefully we can go this coming winter. My health seems better now";

    private const string PriorUser = "The dry desert air there helps my joints more than anything else has.";
    private const string PriorAssistant = "That tracks. Dry air can be the difference between tolerable and miserable.";

    [TestMethod]
    public async Task EstablishedThread_EnrichesQueryWithRecentWindow()
    {
        var ltm = new RecordingLongTermMemory();
        var (builder, _) = BuildBuilder(
            ltm: ltm,
            conversationMemory: ThreadOf(DateTimeOffset.UtcNow, includeCurrentTurn: true));

        await builder.BuildAsync("session-enrich", Reproducer, CancellationToken.None);

        var bm25 = ltm.SearchCalls.FirstOrDefault(c => c.Category is null && c.Query is not null);
        Assert.IsNotNull(bm25, "An LTM BM25 search should have run.");
        StringAssert.StartsWith(bm25.Query, Reproducer,
            "The raw user message must still lead the query.");
        StringAssert.Contains(bm25.Query, "dry desert air",
            "The recent window should be folded into the search query.");
    }

    [TestMethod]
    public async Task EstablishedThread_EnrichesSkillSearchAndEmbedding()
    {
        var skills = new RecordingSkillStore();
        var embeddings = new RecordingEmbeddingGenerator();
        var (builder, _) = BuildBuilder(
            skillStore: skills,
            embeddingGenerator: embeddings,
            conversationMemory: ThreadOf(DateTimeOffset.UtcNow, includeCurrentTurn: true));

        await builder.BuildAsync("session-enrich-skill", Reproducer, CancellationToken.None);

        Assert.AreEqual(1, skills.SearchCalls.Count);
        StringAssert.Contains(skills.SearchCalls[0], "dry desert air",
            "Skill recall should use the same anchored query.");
        Assert.AreEqual(1, embeddings.Inputs.Count);
        StringAssert.Contains(embeddings.Inputs[0], "dry desert air",
            "The shared query embedding must be generated from the enriched query so hybrid " +
            "deployments get the same anchoring as BM25.");
    }

    [TestMethod]
    public async Task FirstTurn_DoesNotEnrich()
    {
        var ltm = new RecordingLongTermMemory();
        // Only the current user turn exists — nothing to anchor to.
        var (builder, _) = BuildBuilder(
            ltm: ltm,
            conversationMemory: new StubConversationMemory(
                [new ConversationTurn("user", Reproducer, DateTimeOffset.UtcNow)]));

        await builder.BuildAsync("session-first", Reproducer, CancellationToken.None);

        var bm25 = ltm.SearchCalls.FirstOrDefault(c => c.Category is null && c.Query is not null);
        Assert.IsNotNull(bm25);
        Assert.AreEqual(Reproducer, bm25.Query,
            "A first-turn message has no thread to anchor to — the query must be the raw message.");
    }

    [TestMethod]
    public async Task StaleThread_DoesNotEnrich()
    {
        var ltm = new RecordingLongTermMemory();
        var (builder, _) = BuildBuilder(
            ltm: ltm,
            // Last turn is well outside ThreadEstablishedRecency.
            conversationMemory: ThreadOf(DateTimeOffset.UtcNow.AddHours(-3), includeCurrentTurn: true));

        await builder.BuildAsync("session-stale", Reproducer, CancellationToken.None);

        var bm25 = ltm.SearchCalls.FirstOrDefault(c => c.Category is null && c.Query is not null);
        Assert.IsNotNull(bm25);
        Assert.AreEqual(Reproducer, bm25.Query,
            "A stale thread is not the conversation the user is continuing — do not anchor to it.");
    }

    [TestMethod]
    public async Task ShortMessage_StaysFullyGated()
    {
        var ltm = new RecordingLongTermMemory();
        var embeddings = new RecordingEmbeddingGenerator();
        var (builder, _) = BuildBuilder(
            ltm: ltm,
            embeddingGenerator: embeddings,
            conversationMemory: ThreadOf(DateTimeOffset.UtcNow, includeCurrentTurn: false));

        await builder.BuildAsync("session-short", "ok", CancellationToken.None);

        Assert.IsFalse(ltm.SearchCalls.Any(c => c.Category is null && c.Query is not null),
            "The 30-char gate from #384 must still suppress the per-turn topic search entirely — " +
            "enrichment must not become a way in.");
        Assert.AreEqual(0, embeddings.Inputs.Count,
            "Short messages must still skip embedding generation.");
    }

    [TestMethod]
    public async Task EnrichmentDoesNotReachTheKnowledgeGraphSeed()
    {
        var graph = new SeedRecordingKnowledgeGraph();
        var (builder, _) = BuildBuilder(
            knowledgeGraph: graph,
            conversationMemory: ThreadOf(DateTimeOffset.UtcNow, includeCurrentTurn: true));

        await builder.BuildAsync("session-graph", Reproducer, CancellationToken.None);

        Assert.AreEqual(1, graph.FindCalls.Count);
        Assert.AreEqual(Reproducer, graph.FindCalls[0],
            "Entity extraction wants the literal message — seeding from prior turns would " +
            "re-traverse entities already in context.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A thread of three prior turns ending at <paramref name="lastTurnAt"/>, optionally
    /// followed by the current user turn (which callers add to conversation memory before
    /// building context, so the builder must discount it).
    /// </summary>
    private static StubConversationMemory ThreadOf(DateTimeOffset lastTurnAt, bool includeCurrentTurn)
    {
        var turns = new List<ConversationTurn>
        {
            new("user", "I've been thinking about the trip to Cathedral City again.", lastTurnAt.AddMinutes(-2)),
            new("user", PriorUser, lastTurnAt.AddMinutes(-1)),
            new("assistant", PriorAssistant, lastTurnAt),
        };

        if (includeCurrentTurn)
            turns.Add(new ConversationTurn("user", Reproducer, lastTurnAt.AddSeconds(1)));

        return new StubConversationMemory(turns);
    }

    private static (AgentContextBuilder Builder, KnowledgeGraphOptions Options) BuildBuilder(
        IKnowledgeGraph? knowledgeGraph = null,
        ILongTermMemory? ltm = null,
        ISkillStore? skillStore = null,
        IConversationMemory? conversationMemory = null,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        var profileHolder = new ProfileHolder();
        var doc = new AgentProfileDocument("soul", null, [], "I am a test agent.");
        profileHolder.Update(new AgentProfile(doc, doc));
        var nameHolder = new AgentNameHolder();

        var agentProfileOptions = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-enrich-test-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(agentProfileOptions.Value.BasePath);

        var clock = new AgentClock(
            new ConfigurationBuilder().Build(),
            agentProfileOptions,
            NullLoggerFactory.Instance.CreateLogger<AgentClock>());

        var graphOpts = new KnowledgeGraphOptions();

        var builder = new AgentContextBuilder(
            profileHolder: profileHolder,
            agent: new AgentIdentity("TestBot"),
            promptBuilder: new DefaultSystemPromptBuilder(profileHolder, nameHolder, Options.Create(new AgentProfileOptions())),
            rulesStore: new StubRulesStore(),
            modelBehavior: ModelBehavior.Default,
            conversationMemory: conversationMemory ?? new StubConversationMemory([]),
            longTermMemory: ltm ?? new RecordingLongTermMemory(),
            injectedMemoryTracker: new InjectedMemoryTracker(),
            workingMemory: new StubWorkingMemory(),
            skillStore: skillStore ?? new RecordingSkillStore(),
            skillIndexTracker: new SkillIndexTracker(),
            skillRecallTracker: new SkillRecallTracker(),
            clock: clock,
            serviceSearchIndexProviders: [],
            knowledgeGraphProviders: knowledgeGraph is null ? [] : [knowledgeGraph],
            knowledgeGraphOptions: Options.Create(graphOpts),
            embeddingGenerators: embeddingGenerator is null ? [] : [embeddingGenerator],
            logger: NullLogger<AgentContextBuilder>.Instance);

        return (builder, graphOpts);
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    private sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<string> Inputs { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var v in values)
            {
                Inputs.Add(v);
                list.Add(new Embedding<float>(new float[] { 0f, 0f, 0f }));
            }
            return Task.FromResult(list);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingLongTermMemory : ILongTermMemory
    {
        public List<MemorySearchCriteria> SearchCalls { get; } = [];

        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            SearchCalls.Add(criteria);
            return Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        }

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryEntry?>(null);

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingSkillStore : ISkillStore
    {
        public List<string> SearchCalls { get; } = [];

        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;

        public Task<IReadOnlyList<Skill>> SearchAsync(
            string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null)
        {
            SearchCalls.Add(query);
            return Task.FromResult<IReadOnlyList<Skill>>([]);
        }
    }

    private sealed class StubConversationMemory(IReadOnlyList<ConversationTurn> turns) : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(turns);

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

    private sealed class StubRulesStore : IRulesStore
    {
        public IReadOnlyList<string> Rules => [];
        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddAsync(string rule) => Task.CompletedTask;
        public Task RemoveAsync(string rule) => Task.CompletedTask;
    }

    private sealed class SeedRecordingKnowledgeGraph : IKnowledgeGraph
    {
        public List<string> FindCalls { get; } = [];

        public Task<IReadOnlyList<KnowledgeEntity>> FindEntitiesByNameAsync(string query, CancellationToken cancellationToken = default)
        {
            FindCalls.Add(query);
            return Task.FromResult<IReadOnlyList<KnowledgeEntity>>([]);
        }

        public Task<IReadOnlyList<KnowledgeTriple>> TraverseAsync(IReadOnlyList<string> seedEntityIds, int maxHops = 2, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeTriple>>([]);

        public Task TouchEntitiesAsync(IReadOnlyList<string> entityIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveEntityAsync(KnowledgeEntity entity, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<KnowledgeEntity?> GetEntityAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<KnowledgeEntity?>(null);
        public Task DeleteEntityAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<KnowledgeEntity>> ListEntitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeEntity>>([]);
        public Task SaveTripleAsync(KnowledgeTriple triple, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<KnowledgeTriple>> GetTriplesForSubjectAsync(string subjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeTriple>>([]);
        public Task<IReadOnlyList<KnowledgeTriple>> GetTriplesForObjectAsync(string objectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeTriple>>([]);
        public Task DeleteTripleAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<KnowledgeTriple>> ListTriplesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeTriple>>([]);
    }
}
