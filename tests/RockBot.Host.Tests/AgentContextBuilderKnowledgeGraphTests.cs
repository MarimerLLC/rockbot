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
/// Exercises the memory-derived knowledge-graph seeding added in #285.
/// These tests wire <see cref="AgentContextBuilder"/> up with in-memory stubs and
/// verify the combined triple list composition (user-seed first, memory-seed second,
/// deduped by triple ID, capped at <c>MaxExpandedTriples</c>).
/// </summary>
[TestClass]
public class AgentContextBuilderKnowledgeGraphTests
{
    // ── Tests ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task MemorySeeds_Contribute_WhenUserMessageLacksEntities()
    {
        // User message does not mention any entity. Top memory does → its entity
        // seeds a 1-hop expansion.
        var graph = new StubKnowledgeGraph();
        var alice = graph.AddEntity("e-alice", "Alice");
        var project = graph.AddEntity("e-project", "RockBot");
        graph.AddTriple("t1", alice, "works_on", project);

        var ltm = new StubLongTermMemory([
            Memory("m1", "Alice is the lead on that initiative"),
        ]);

        var (builder, _) = BuildBuilder(graph, ltm);

        var messages = await builder.BuildAsync("session-1", "What was the decision last week?", CancellationToken.None);

        var graphBlock = FindSystemMessageStartingWith(messages, "Related knowledge graph connections");
        Assert.IsNotNull(graphBlock, "Memory-derived seeds should produce graph-expansion context");
        StringAssert.Contains(graphBlock, "works_on");
    }

    [TestMethod]
    public async Task UserSeedTriples_FillBudgetFirst_WhenCapReached()
    {
        // Budget is tiny (cap=2). User-seed traversal yields >2 triples; memory-seed
        // triples must not displace them.
        var graph = new StubKnowledgeGraph();
        var alice = graph.AddEntity("e-alice", "Alice");
        var rockbot = graph.AddEntity("e-rockbot", "RockBot");
        var bob = graph.AddEntity("e-bob", "Bob");
        // User-seed entity is Alice; her triples dominate the budget.
        graph.AddTriple("u1", alice, "works_on", rockbot);
        graph.AddTriple("u2", alice, "knows", bob);
        graph.AddTriple("u3", alice, "uses", graph.AddEntity("e-tool", "DotNet"));
        // Memory-seed entity is Carol — entirely disjoint from Alice's neighborhood.
        var carol = graph.AddEntity("e-carol", "Carol");
        var dave = graph.AddEntity("e-dave", "Dave");
        graph.AddTriple("m1", carol, "mentors", dave);

        var ltm = new StubLongTermMemory([
            Memory("m1", "Carol has been mentoring Dave this quarter"),
        ]);

        var (builder, _) = BuildBuilder(graph, ltm, opts =>
        {
            opts.MaxExpandedTriples = 2;
            opts.MaxHops = 2;
            opts.MaxMemorySeedSources = 2;
            opts.MemorySeedMaxHops = 1;
        });

        var messages = await builder.BuildAsync("session-2", "tell me about Alice", CancellationToken.None);

        var block = FindSystemMessageStartingWith(messages, "Related knowledge graph connections");
        Assert.IsNotNull(block);
        // With cap=2 and three Alice triples, the memory-seed triple (mentors) must be excluded.
        Assert.IsFalse(block.Contains("mentors"),
            "Memory-seed triple should not crowd out user-seed triples when cap is reached");
    }

    [TestMethod]
    public async Task MemorySeedEntities_Overlapping_UserSeedEntities_AreNotDoubleCounted()
    {
        // When the top memory mentions an entity already in the user-seed set,
        // FindEntities on the memory will surface it too — but we must skip it so we
        // don't traverse twice (or produce duplicate triples in the combined list).
        var graph = new StubKnowledgeGraph();
        var alice = graph.AddEntity("e-alice", "Alice");
        var rockbot = graph.AddEntity("e-rockbot", "RockBot");
        graph.AddTriple("t1", alice, "works_on", rockbot);

        var ltm = new StubLongTermMemory([
            Memory("m1", "Alice was on the call last Tuesday"),
        ]);

        var (builder, _) = BuildBuilder(graph, ltm);

        await builder.BuildAsync("session-3", "what did Alice think?", CancellationToken.None);

        // FindEntitiesByNameAsync is called once for the user message and once per top memory
        // (K=2 by default, but only 1 memory is in the store here, so just 1 memory call).
        // Alice must appear in the user-seed traversal exactly once — not once per source.
        Assert.AreEqual(2, graph.FindCalls.Count, "Expected one find-call for user text, one for the memory");
        // Alice is the single user-seed. Traversals: one user-seed traversal (Alice).
        // The memory traversal should have an EMPTY seed list (Alice excluded as duplicate),
        // so no traversal is kicked off — we assert that below.
        Assert.AreEqual(1, graph.TraverseCalls.Count, "Memory traversal should be skipped when all entities are already user-seeds");
        CollectionAssert.AreEquivalent(new[] { "e-alice" }, graph.TraverseCalls[0].SeedIds.ToArray());
    }

    [TestMethod]
    public async Task MaxMemorySeedSources_Zero_DisablesMemorySeeding()
    {
        var graph = new StubKnowledgeGraph();
        graph.AddEntity("e-alice", "Alice");

        var ltm = new StubLongTermMemory([
            Memory("m1", "Alice shipped the release"),
        ]);

        var (builder, _) = BuildBuilder(graph, ltm, opts => opts.MaxMemorySeedSources = 0);

        await builder.BuildAsync("session-4", "what's up?", CancellationToken.None);

        // Only the user-message FindEntities call should happen. No memory lookups.
        Assert.AreEqual(1, graph.FindCalls.Count);
        Assert.AreEqual("what's up?", graph.FindCalls[0]);
    }

    [TestMethod]
    public async Task NoKnowledgeGraph_Configured_NoGraphCallsAtAll()
    {
        var ltm = new StubLongTermMemory([
            Memory("m1", "some memory content"),
        ]);

        var (builder, _) = BuildBuilder(knowledgeGraph: null, ltm);

        var messages = await builder.BuildAsync("session-5", "hello", CancellationToken.None);

        Assert.IsNull(FindSystemMessageStartingWith(messages, "Related knowledge graph connections"));
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
        IKnowledgeGraph? knowledgeGraph,
        ILongTermMemory ltm,
        Action<KnowledgeGraphOptions>? configure = null)
    {
        var profileHolder = new ProfileHolder();
        var doc = new AgentProfileDocument("soul", null, [], "I am a test agent.");
        profileHolder.Update(new AgentProfile(doc, doc));
        var nameHolder = new AgentNameHolder();

        var agentProfileOptions = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-ctxbuilder-test-" + Guid.NewGuid().ToString("N"))
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
            promptBuilder: new DefaultSystemPromptBuilder(profileHolder, nameHolder, Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions())),
            rulesStore: new StubRulesStore(),
            modelBehavior: ModelBehavior.Default,
            conversationMemory: new StubConversationMemory(),
            longTermMemory: ltm,
            injectedMemoryTracker: new InjectedMemoryTracker(),
            workingMemory: new StubWorkingMemory(),
            skillStore: new StubSkillStore(),
            skillIndexTracker: new SkillIndexTracker(),
            skillRecallTracker: new SkillRecallTracker(),
            clock: clock,
            serviceSearchIndexProviders: [],
            knowledgeGraphProviders: knowledgeGraph is null ? [] : [knowledgeGraph],
            knowledgeGraphOptions: Options.Create(graphOpts),
            embeddingGenerators: [],
            logger: NullLogger<AgentContextBuilder>.Instance);

        return (builder, graphOpts);
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal in-memory knowledge graph that supports the subset of operations
    /// AgentContextBuilder invokes. Records every call for assertions.
    /// </summary>
    private sealed class StubKnowledgeGraph : IKnowledgeGraph
    {
        private readonly Dictionary<string, KnowledgeEntity> _entities = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, KnowledgeTriple> _triples = new(StringComparer.OrdinalIgnoreCase);

        public List<string> FindCalls { get; } = [];
        public List<(IReadOnlyList<string> SeedIds, int MaxHops)> TraverseCalls { get; } = [];

        public string AddEntity(string id, string name, params string[] aliases)
        {
            _entities[id] = new KnowledgeEntity(id, name, KnowledgeEntityType.Person, aliases, null, DateTimeOffset.UtcNow);
            return id;
        }

        public void AddTriple(string id, string subject, string predicate, string @object)
        {
            _triples[id] = new KnowledgeTriple(id, subject, predicate, @object, 1.0f, null, DateTimeOffset.UtcNow);
        }

        public Task<IReadOnlyList<KnowledgeEntity>> FindEntitiesByNameAsync(string query, CancellationToken cancellationToken = default)
        {
            FindCalls.Add(query);
            var lower = query.ToLowerInvariant();
            var matches = _entities.Values
                .Where(e => lower.Contains(e.Name.ToLowerInvariant()) ||
                            e.Aliases.Any(a => lower.Contains(a.ToLowerInvariant())))
                .ToList();
            return Task.FromResult<IReadOnlyList<KnowledgeEntity>>(matches);
        }

        public Task<IReadOnlyList<KnowledgeTriple>> TraverseAsync(
            IReadOnlyList<string> seedEntityIds, int maxHops = 2, CancellationToken cancellationToken = default)
        {
            TraverseCalls.Add((seedEntityIds.ToList(), maxHops));
            // Simple BFS: collect every triple with at least one endpoint in the seed set.
            var results = _triples.Values
                .Where(t => seedEntityIds.Contains(t.Subject) || seedEntityIds.Contains(t.Object))
                .ToList();
            return Task.FromResult<IReadOnlyList<KnowledgeTriple>>(results);
        }

        public Task TouchEntitiesAsync(IReadOnlyList<string> entityIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // Unused by AgentContextBuilder — left as no-ops.
        public Task SaveEntityAsync(KnowledgeEntity entity, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<KnowledgeEntity?> GetEntityAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<KnowledgeEntity?>(_entities.GetValueOrDefault(id));
        public Task DeleteEntityAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<KnowledgeEntity>> ListEntitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeEntity>>(_entities.Values.ToList());
        public Task SaveTripleAsync(KnowledgeTriple triple, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<KnowledgeTriple>> GetTriplesForSubjectAsync(string subjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeTriple>>([]);
        public Task<IReadOnlyList<KnowledgeTriple>> GetTriplesForObjectAsync(string objectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeTriple>>([]);
        public Task DeleteTripleAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<KnowledgeTriple>> ListTriplesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeTriple>>(_triples.Values.ToList());
    }

    private sealed class StubLongTermMemory(IReadOnlyList<MemoryEntry> recall) : ILongTermMemory
    {
        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default)
        {
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

    private sealed class StubRulesStore : IRulesStore
    {
        public IReadOnlyList<string> Rules => [];
        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddAsync(string rule) => Task.CompletedTask;
        public Task RemoveAsync(string rule) => Task.CompletedTask;
    }
}
