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
/// Covers the short-message gate in <see cref="AgentContextBuilder"/> introduced
/// in PR #384 (issue #383). When the incoming user message is at or below
/// <see cref="ShortMessageHeuristics.UserMessageCharThreshold"/> characters, the
/// builder must skip per-turn topic-search injections (BM25 long-term memory,
/// episodic memory, skill recall, service hints, knowledge-graph memory seeds)
/// AND the embedding generation that backs them, while preserving session
/// grounding (conversation history, rules, identity, working memory).
/// </summary>
[TestClass]
public class AgentContextBuilderShortMessageGateTests
{
    // 18-char production reproducer from the #383 incident.
    private const string ShortMessage = "I'll find out soon";

    // Long enough to clear the threshold by a comfortable margin.
    private const string LongMessage = "What did the user ask Bob about the meeting earlier?";

    // ── Embedding generation gate ────────────────────────────────────────────

    [TestMethod]
    public async Task ShortMessage_DoesNotGenerateQueryEmbedding()
    {
        var embeddings = new CountingEmbeddingGenerator();
        var (builder, _) = BuildBuilder(embeddingGenerator: embeddings);

        await builder.BuildAsync("session-short-emb", ShortMessage, CancellationToken.None);

        Assert.AreEqual(0, embeddings.CallCount,
            "Short user message must not trigger query embedding generation.");
    }

    [TestMethod]
    public async Task LongMessage_GeneratesQueryEmbedding()
    {
        var embeddings = new CountingEmbeddingGenerator();
        var (builder, _) = BuildBuilder(embeddingGenerator: embeddings);

        await builder.BuildAsync("session-long-emb", LongMessage, CancellationToken.None);

        Assert.AreEqual(1, embeddings.CallCount,
            "Long user message should generate exactly one shared query embedding.");
    }

    // ── Long-term, episodic, skill recall gates ──────────────────────────────

    [TestMethod]
    public async Task ShortMessage_SkipsLtmAndEpisodicAndSkillRecall()
    {
        var ltm = new RecordingLongTermMemory(
            recall: [Memory("m1", "some lexically-noisy hit")]);
        var skills = new RecordingSkillStore(
            search: [new Skill("noisy-skill", "summary", "content", DateTimeOffset.UtcNow)]);

        var (builder, _) = BuildBuilder(ltm: ltm, skillStore: skills);

        var messages = await builder.BuildAsync("session-short-recall", ShortMessage, CancellationToken.None);

        // No category-less BM25 LTM search.
        Assert.IsFalse(
            ltm.SearchCalls.Any(c => c.Category is null && c.Query == ShortMessage),
            "Short message must not issue an LTM BM25 search keyed on its text.");

        // No episodic search (Category == "episodic").
        Assert.IsFalse(
            ltm.SearchCalls.Any(c => string.Equals(c.Category, "episodic", StringComparison.Ordinal)),
            "Short message must not issue an episodic memory search.");

        // No per-turn skill recall.
        Assert.AreEqual(0, skills.SearchCalls.Count,
            "Short message must not issue a per-turn skill BM25 search.");

        // And nothing from the recall lists should have ended up in the prompt.
        Assert.IsNull(FindSystemMessageStartingWith(messages, "Recalled from long-term memory"),
            "Short message must not produce an LTM recall system message.");
        Assert.IsNull(FindSystemMessageStartingWith(messages, "Relevant past experiences"),
            "Short message must not produce an episodic system message.");
    }

    [TestMethod]
    public async Task LongMessage_RunsFullRecallPipeline()
    {
        var ltm = new RecordingLongTermMemory(
            recall: [Memory("m1", "directly relevant prior context")]);
        var skills = new RecordingSkillStore(
            search: [new Skill("on-topic-skill", "summary", "content", DateTimeOffset.UtcNow)]);

        var (builder, _) = BuildBuilder(ltm: ltm, skillStore: skills);

        var messages = await builder.BuildAsync("session-long-recall", LongMessage, CancellationToken.None);

        Assert.IsTrue(
            ltm.SearchCalls.Any(c => c.Category is null && c.Query == LongMessage),
            "Long message must issue an LTM BM25 search keyed on its text.");
        Assert.IsTrue(
            ltm.SearchCalls.Any(c => string.Equals(c.Category, "episodic", StringComparison.Ordinal)),
            "Long message must issue an episodic memory search.");
        Assert.AreEqual(1, skills.SearchCalls.Count,
            "Long message must issue a per-turn skill BM25 search.");

        Assert.IsNotNull(FindSystemMessageStartingWith(messages, "Recalled from long-term memory"),
            "Long message should inject an LTM recall system message when memories were returned.");
    }

    // ── Knowledge graph memory-seed branch ───────────────────────────────────

    [TestMethod]
    public async Task ShortMessage_SkipsKnowledgeGraphMemorySeedExpansion()
    {
        var graph = new SeedTrackingKnowledgeGraph();
        var ltm = new RecordingLongTermMemory(
            recall: [Memory("m1", "Alice owns the migration")]);

        var (builder, opts) = BuildBuilder(knowledgeGraph: graph, ltm: ltm,
            configure: o =>
            {
                o.MaxMemorySeedSources = 2;
                o.MemorySeedMaxHops = 1;
            });

        await builder.BuildAsync("session-short-kg", ShortMessage, CancellationToken.None);

        // User-seed lookup ALWAYS runs (FindEntitiesByNameAsync is called regardless of
        // length). But the memory-seed expansion path requires `recalled` to be
        // populated, which the short-message gate clears — so the per-memory
        // `FindEntities` call from inside the memory-seed loop must not fire.
        Assert.AreEqual(1, graph.FindCalls.Count,
            "Only the user-seed FindEntities call should run; the memory-seed loop must not iterate on short messages.");
        Assert.AreEqual(ShortMessage, graph.FindCalls[0]);
    }

    // ── First-turn fallback ──────────────────────────────────────────────────

    [TestMethod]
    public async Task ShortMessage_FirstTurnFallback_StillRunsUnqueriedLtmSearch()
    {
        // The fallback at AgentContextBuilder line ~219 fires when recalled is empty
        // AND history.Count == 1. After the short-message gate clears recalled,
        // a brand-new session with no prior turns should still pick up identity-style
        // entries via the un-queried fallback search.
        var ltm = new RecordingLongTermMemory(recall: []);
        var conversation = new SingleTurnConversationMemory();

        var (builder, _) = BuildBuilder(ltm: ltm, conversationMemory: conversation);

        await builder.BuildAsync("session-first-turn", ShortMessage, CancellationToken.None);

        // The fallback issues SearchAsync with NO Query and NO Category — only MaxResults.
        Assert.IsTrue(
            ltm.SearchCalls.Any(c => c.Query is null && c.Category is null),
            "First-turn fallback must still run an un-queried LTM search even on short messages.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MemoryEntry Memory(string id, string content) =>
        new(Id: id,
            Content: content,
            Category: null,
            Tags: [],
            CreatedAt: DateTimeOffset.UtcNow);

    private static string? FindSystemMessageStartingWith(IEnumerable<ChatMessage> messages, string prefix)
    {
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.System && m.Text is { } t && t.StartsWith(prefix, StringComparison.Ordinal))
                return t;
        }
        return null;
    }

    private static (AgentContextBuilder Builder, KnowledgeGraphOptions Options) BuildBuilder(
        IKnowledgeGraph? knowledgeGraph = null,
        ILongTermMemory? ltm = null,
        ISkillStore? skillStore = null,
        IConversationMemory? conversationMemory = null,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        Action<KnowledgeGraphOptions>? configure = null)
    {
        var profileHolder = new ProfileHolder();
        var doc = new AgentProfileDocument("soul", null, [], "I am a test agent.");
        profileHolder.Update(new AgentProfile(doc, doc));
        var nameHolder = new AgentNameHolder();

        var agentProfileOptions = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-shortmsg-test-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(agentProfileOptions.Value.BasePath);

        var clock = new AgentClock(
            new ConfigurationBuilder().Build(),
            agentProfileOptions,
            NullLoggerFactory.Instance.CreateLogger<AgentClock>());

        var graphOpts = new KnowledgeGraphOptions();
        configure?.Invoke(graphOpts);

        var builder = new AgentContextBuilder(
            profileHolder: profileHolder,
            agent: new AgentIdentity("TestBot"),
            promptBuilder: new DefaultSystemPromptBuilder(profileHolder, nameHolder, Options.Create(new AgentProfileOptions())),
            rulesStore: new StubRulesStore(),
            modelBehavior: ModelBehavior.Default,
            conversationMemory: conversationMemory ?? new StubConversationMemory(),
            longTermMemory: ltm ?? new RecordingLongTermMemory(recall: []),
            injectedMemoryTracker: new InjectedMemoryTracker(),
            workingMemory: new StubWorkingMemory(),
            skillStore: skillStore ?? new RecordingSkillStore(search: []),
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

    private sealed class CountingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int CallCount { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var list = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                list.Add(new Embedding<float>(new float[] { 0f, 0f, 0f }));
            return Task.FromResult(list);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingLongTermMemory(IReadOnlyList<MemoryEntry> recall) : ILongTermMemory
    {
        public List<MemorySearchCriteria> SearchCalls { get; } = [];

        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            SearchCalls.Add(criteria);
            // Mirror StubLongTermMemory's behaviour: category-scoped searches return empty
            // (they're for identity/episodic and aren't the BM25 query path under test).
            if (criteria.Category is not null)
                return Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
            return Task.FromResult(recall);
        }

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryEntry?>(null);

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingSkillStore(IReadOnlyList<Skill> search) : ISkillStore
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
            return Task.FromResult(search);
        }
    }

    private sealed class SingleTurnConversationMemory : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            // First turn: exactly one "user" turn already recorded by the time BuildAsync reads history.
            Task.FromResult<IReadOnlyList<ConversationTurn>>(
                [new ConversationTurn("user", "I'll find out soon", DateTimeOffset.UtcNow)]);

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
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

    private sealed class StubRulesStore : IRulesStore
    {
        public IReadOnlyList<string> Rules => [];
        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddAsync(string rule) => Task.CompletedTask;
        public Task RemoveAsync(string rule) => Task.CompletedTask;
    }

    /// <summary>
    /// Knowledge graph that counts FindEntitiesByNameAsync calls so the test can
    /// distinguish "user-seed lookup only" from "user-seed + memory-seed loop".
    /// </summary>
    private sealed class SeedTrackingKnowledgeGraph : IKnowledgeGraph
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
