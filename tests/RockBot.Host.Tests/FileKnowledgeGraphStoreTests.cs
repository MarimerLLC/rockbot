using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class FileKnowledgeGraphStoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-kg-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Entity CRUD ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SaveEntityAsync_And_GetEntityAsync_RoundTrips()
    {
        var store = CreateStore();
        var entity = CreateEntity("e1", "Alice", KnowledgeEntityType.Person);

        await store.SaveEntityAsync(entity);
        var result = await store.GetEntityAsync("e1");

        Assert.IsNotNull(result);
        Assert.AreEqual("e1", result.Id);
        Assert.AreEqual("Alice", result.Name);
        Assert.AreEqual(KnowledgeEntityType.Person, result.EntityType);
    }

    [TestMethod]
    public async Task GetEntityAsync_ReturnsNull_WhenNotFound()
    {
        var store = CreateStore();
        var result = await store.GetEntityAsync("nonexistent");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task SaveEntityAsync_OverwritesExisting()
    {
        var store = CreateStore();
        await store.SaveEntityAsync(CreateEntity("e1", "Alice", KnowledgeEntityType.Person));
        await store.SaveEntityAsync(CreateEntity("e1", "Alice Smith", KnowledgeEntityType.Person));

        var result = await store.GetEntityAsync("e1");

        Assert.IsNotNull(result);
        Assert.AreEqual("Alice Smith", result.Name);
    }

    [TestMethod]
    public async Task DeleteEntityAsync_RemovesEntity()
    {
        var store = CreateStore();
        await store.SaveEntityAsync(CreateEntity("e1", "Alice", KnowledgeEntityType.Person));

        await store.DeleteEntityAsync("e1");
        var result = await store.GetEntityAsync("e1");

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task DeleteEntityAsync_RemovesRelatedTriples()
    {
        var store = CreateStore();
        await store.SaveEntityAsync(CreateEntity("e1", "Alice", KnowledgeEntityType.Person));
        await store.SaveEntityAsync(CreateEntity("e2", "RockBot", KnowledgeEntityType.Project));
        await store.SaveTripleAsync(CreateTriple("t1", "e1", "works_on", "e2"));

        await store.DeleteEntityAsync("e1");

        var triples = await store.GetTriplesForSubjectAsync("e1");
        Assert.AreEqual(0, triples.Count);
    }

    [TestMethod]
    public async Task DeleteEntityAsync_NoOp_WhenNotFound()
    {
        var store = CreateStore();
        await store.DeleteEntityAsync("nonexistent"); // Should not throw
    }

    [TestMethod]
    public async Task ListEntitiesAsync_ReturnsAll()
    {
        var store = CreateStore();
        await store.SaveEntityAsync(CreateEntity("e1", "Alice", KnowledgeEntityType.Person));
        await store.SaveEntityAsync(CreateEntity("e2", "RockBot", KnowledgeEntityType.Project));

        var all = await store.ListEntitiesAsync();

        Assert.AreEqual(2, all.Count);
    }

    // ── Entity name search ───────────────────────────────────────────────────

    [TestMethod]
    public async Task FindEntitiesByNameAsync_MatchesWholeWordInQuery()
    {
        var store = CreateStore();
        await store.SaveEntityAsync(CreateEntity("e1", "Alice", KnowledgeEntityType.Person));
        await store.SaveEntityAsync(CreateEntity("e2", "Bob", KnowledgeEntityType.Person));

        var results = await store.FindEntitiesByNameAsync("I talked to Alice yesterday");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("e1", results[0].Id);
    }

    [TestMethod]
    public async Task FindEntitiesByNameAsync_DoesNotMatchSubstring()
    {
        var store = CreateStore();
        await store.SaveEntityAsync(CreateEntity("e1", "Alice", KnowledgeEntityType.Person));

        // "Alice" should NOT match inside "Malice"
        var results = await store.FindEntitiesByNameAsync("Malice aforethought");

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task FindEntitiesByNameAsync_SkipsShortNames()
    {
        var store = CreateStore();
        await store.SaveEntityAsync(CreateEntity("e1", "AI", KnowledgeEntityType.Topic));
        await store.SaveEntityAsync(CreateEntity("e2", "PR", KnowledgeEntityType.Topic));

        // Two-letter entity names are below MinEntityNameLength (3) — should not match
        var results = await store.FindEntitiesByNameAsync("the AI created a PR for review");

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task FindEntitiesByNameAsync_MatchesByAlias()
    {
        var store = CreateStore();
        var entity = new KnowledgeEntity("e1", "Alice Smith", KnowledgeEntityType.Person,
            Aliases: ["A. Smith"], Metadata: null, CreatedAt: DateTimeOffset.UtcNow);
        await store.SaveEntityAsync(entity);

        var results = await store.FindEntitiesByNameAsync("ask A. Smith about it");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("e1", results[0].Id);
    }

    [TestMethod]
    public async Task FindEntitiesByNameAsync_SkipsShortAliases()
    {
        var store = CreateStore();
        var entity = new KnowledgeEntity("e1", "Alice Smith", KnowledgeEntityType.Person,
            Aliases: ["AS"], Metadata: null, CreatedAt: DateTimeOffset.UtcNow);
        await store.SaveEntityAsync(entity);

        // Short alias "AS" should be ignored
        var results = await store.FindEntitiesByNameAsync("AS mentioned earlier");

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task FindEntitiesByNameAsync_CaseInsensitive()
    {
        var store = CreateStore();
        await store.SaveEntityAsync(CreateEntity("e1", "RockBot", KnowledgeEntityType.Project));

        var results = await store.FindEntitiesByNameAsync("tell me about rockbot");

        Assert.AreEqual(1, results.Count);
    }

    [TestMethod]
    public async Task FindEntitiesByNameAsync_MultiWordEntityName()
    {
        var store = CreateStore();
        await store.SaveEntityAsync(CreateEntity("e1", "Azure DevOps", KnowledgeEntityType.Tool));

        var results = await store.FindEntitiesByNameAsync("deploy via Azure DevOps pipeline");

        Assert.AreEqual(1, results.Count);
    }

    // ── Triple CRUD ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SaveTripleAsync_And_GetTriplesForSubjectAsync_RoundTrips()
    {
        var store = CreateStore();
        var triple = CreateTriple("t1", "Alice", "works_on", "RockBot");

        await store.SaveTripleAsync(triple);
        var results = await store.GetTriplesForSubjectAsync("Alice");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("works_on", results[0].Predicate);
        Assert.AreEqual("RockBot", results[0].Object);
    }

    [TestMethod]
    public async Task GetTriplesForObjectAsync_ReturnsIncomingRelationships()
    {
        var store = CreateStore();
        await store.SaveTripleAsync(CreateTriple("t1", "Alice", "works_on", "RockBot"));
        await store.SaveTripleAsync(CreateTriple("t2", "Bob", "works_on", "RockBot"));

        var results = await store.GetTriplesForObjectAsync("RockBot");

        Assert.AreEqual(2, results.Count);
    }

    [TestMethod]
    public async Task DeleteTripleAsync_RemovesTriple()
    {
        var store = CreateStore();
        await store.SaveTripleAsync(CreateTriple("t1", "Alice", "works_on", "RockBot"));

        await store.DeleteTripleAsync("t1");
        var results = await store.GetTriplesForSubjectAsync("Alice");

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task DeleteTripleAsync_NoOp_WhenNotFound()
    {
        var store = CreateStore();
        await store.DeleteTripleAsync("nonexistent"); // Should not throw
    }

    // ── Traversal ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task TraverseAsync_OneHop_ReturnsDirectConnections()
    {
        var store = CreateStore();
        await store.SaveTripleAsync(CreateTriple("t1", "Alice", "works_on", "RockBot"));
        await store.SaveTripleAsync(CreateTriple("t2", "RockBot", "uses", "RabbitMQ"));
        await store.SaveTripleAsync(CreateTriple("t3", "Bob", "works_on", "OtherProject"));

        var results = await store.TraverseAsync(["Alice"], maxHops: 1);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("t1", results[0].Id);
    }

    [TestMethod]
    public async Task TraverseAsync_TwoHops_ReturnsTransitiveConnections()
    {
        var store = CreateStore();
        await store.SaveTripleAsync(CreateTriple("t1", "Alice", "works_on", "RockBot"));
        await store.SaveTripleAsync(CreateTriple("t2", "RockBot", "uses", "RabbitMQ"));
        await store.SaveTripleAsync(CreateTriple("t3", "Bob", "works_on", "OtherProject"));

        var results = await store.TraverseAsync(["Alice"], maxHops: 2);

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Any(t => t.Id == "t1"));
        Assert.IsTrue(results.Any(t => t.Id == "t2"));
    }

    [TestMethod]
    public async Task TraverseAsync_HandlesCycles()
    {
        var store = CreateStore();
        await store.SaveTripleAsync(CreateTriple("t1", "A", "knows", "B"));
        await store.SaveTripleAsync(CreateTriple("t2", "B", "knows", "A"));

        var results = await store.TraverseAsync(["A"], maxHops: 3);

        // Should not infinite loop; returns both triples exactly once
        Assert.AreEqual(2, results.Count);
    }

    [TestMethod]
    public async Task TraverseAsync_MultipleSeedEntities()
    {
        var store = CreateStore();
        await store.SaveTripleAsync(CreateTriple("t1", "Alice", "works_on", "RockBot"));
        await store.SaveTripleAsync(CreateTriple("t2", "Bob", "works_on", "OtherProject"));

        var results = await store.TraverseAsync(["Alice", "Bob"], maxHops: 1);

        Assert.AreEqual(2, results.Count);
    }

    [TestMethod]
    public async Task TraverseAsync_EmptySeeds_ReturnsEmpty()
    {
        var store = CreateStore();
        await store.SaveTripleAsync(CreateTriple("t1", "Alice", "works_on", "RockBot"));

        var results = await store.TraverseAsync([], maxHops: 2);

        Assert.AreEqual(0, results.Count);
    }

    // ── TraverseCore unit tests ──────────────────────────────────────────────

    [TestMethod]
    public void TraverseCore_EmptyGraph_ReturnsEmpty()
    {
        var triples = new Dictionary<string, KnowledgeTriple>(StringComparer.OrdinalIgnoreCase);
        var result = FileKnowledgeGraphStore.TraverseCore(triples, ["A"], 2);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void TraverseCore_ZeroHops_ReturnsEmpty()
    {
        var triples = new Dictionary<string, KnowledgeTriple>(StringComparer.OrdinalIgnoreCase)
        {
            ["t1"] = CreateTriple("t1", "A", "knows", "B")
        };
        var result = FileKnowledgeGraphStore.TraverseCore(triples, ["A"], 0);
        Assert.AreEqual(0, result.Count);
    }

    // ── ContainsWholePhrase unit tests ─────────────────────────────────────

    [TestMethod]
    public void ContainsWholePhrase_MatchesExactWord()
    {
        Assert.IsTrue(FileKnowledgeGraphStore.ContainsWholePhrase("ask alice about it", "alice"));
    }

    [TestMethod]
    public void ContainsWholePhrase_DoesNotMatchSubstring()
    {
        Assert.IsFalse(FileKnowledgeGraphStore.ContainsWholePhrase("malice aforethought", "alice"));
    }

    [TestMethod]
    public void ContainsWholePhrase_MatchesAtStart()
    {
        Assert.IsTrue(FileKnowledgeGraphStore.ContainsWholePhrase("alice is here", "alice"));
    }

    [TestMethod]
    public void ContainsWholePhrase_MatchesAtEnd()
    {
        Assert.IsTrue(FileKnowledgeGraphStore.ContainsWholePhrase("talk to alice", "alice"));
    }

    [TestMethod]
    public void ContainsWholePhrase_MatchesMultiWordPhrase()
    {
        Assert.IsTrue(FileKnowledgeGraphStore.ContainsWholePhrase("deploy via azure devops pipeline", "azure devops"));
    }

    [TestMethod]
    public void ContainsWholePhrase_EmptyKeyword_ReturnsFalse()
    {
        Assert.IsFalse(FileKnowledgeGraphStore.ContainsWholePhrase("some text", ""));
    }

    // ── MatchesName unit tests ──────────────────────────────────────────────

    [TestMethod]
    public void MatchesName_SkipsBelowMinLength()
    {
        var entity = new KnowledgeEntity("e1", "AI", KnowledgeEntityType.Topic,
            Aliases: [], Metadata: null, CreatedAt: DateTimeOffset.UtcNow);
        Assert.IsFalse(FileKnowledgeGraphStore.MatchesName(entity, "the AI is great"));
    }

    [TestMethod]
    public void MatchesName_MatchesAtMinLength()
    {
        var entity = new KnowledgeEntity("e1", "Bob", KnowledgeEntityType.Person,
            Aliases: [], Metadata: null, CreatedAt: DateTimeOffset.UtcNow);
        Assert.IsTrue(FileKnowledgeGraphStore.MatchesName(entity, "ask Bob about it"));
    }

    [TestMethod]
    public void MatchesName_RequiresWordBoundary()
    {
        var entity = new KnowledgeEntity("e1", "calendar", KnowledgeEntityType.Tool,
            Aliases: [], Metadata: null, CreatedAt: DateTimeOffset.UtcNow);
        // "calendar" should match as a standalone word
        Assert.IsTrue(FileKnowledgeGraphStore.MatchesName(entity, "check the calendar for events"));
        // but not inside another word
        Assert.IsFalse(FileKnowledgeGraphStore.MatchesName(entity, "calendarWidget is broken"));
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Entities_PersistAcrossInstances()
    {
        var store1 = CreateStore();
        await store1.SaveEntityAsync(CreateEntity("e1", "Alice", KnowledgeEntityType.Person));

        // Create new store instance pointing to same directory
        var store2 = CreateStore();
        var result = await store2.GetEntityAsync("e1");

        Assert.IsNotNull(result);
        Assert.AreEqual("Alice", result.Name);
    }

    [TestMethod]
    public async Task Triples_PersistAcrossInstances()
    {
        var store1 = CreateStore();
        await store1.SaveTripleAsync(CreateTriple("t1", "Alice", "works_on", "RockBot"));

        var store2 = CreateStore();
        var results = await store2.GetTriplesForSubjectAsync("Alice");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("works_on", results[0].Predicate);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private FileKnowledgeGraphStore CreateStore()
    {
        return new FileKnowledgeGraphStore(
            Options.Create(new KnowledgeGraphOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            NullLogger<FileKnowledgeGraphStore>.Instance);
    }

    private static KnowledgeEntity CreateEntity(
        string id,
        string name,
        KnowledgeEntityType entityType)
    {
        return new KnowledgeEntity(id, name, entityType, Aliases: [], Metadata: null, CreatedAt: DateTimeOffset.UtcNow);
    }

    private static KnowledgeTriple CreateTriple(
        string id,
        string subject,
        string predicate,
        string @object,
        float confidence = 0.8f)
    {
        return new KnowledgeTriple(id, subject, predicate, @object, confidence, SourceEpisodeId: null, CreatedAt: DateTimeOffset.UtcNow);
    }
}
