using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers the store-side lookup that lets a save reinforce an existing entry instead of adding a
/// near-copy of it.
/// </summary>
/// <remarks>
/// No embedding generator is registered, so every case here exercises the lexical fallback. That
/// is deliberate: the framework supports BM25-only deployments, and the fallback is the path that
/// decides whether they get deduplication at all.
/// </remarks>
[TestClass]
public class FileMemoryStoreSimilarityLookupTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init() =>
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-similarity-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task FindMostSimilarAsync_ReturnsTheNearVerbatimEntry_WithALexicalScore()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("existing", "Rocky prefers concise status"));
        await store.SaveAsync(Entry("unrelated", "Longhorn backup retention spans roughly four days"));

        var match = await store.FindMostSimilarAsync(Entry("candidate", "Rocky prefers concise"));

        Assert.IsNotNull(match);
        Assert.AreEqual("existing", match.Entry.Id);
        Assert.AreEqual(MemorySimilarityMeasure.Lexical, match.Measure);

        // Three shared tokens over a four-token union. Asserted exactly because the deduplicator's
        // default threshold is calibrated against this scale, not against cosine's.
        Assert.AreEqual(0.75, match.Score, 0.001);
    }

    [TestMethod]
    public async Task FindMostSimilarAsync_IgnoresEntriesInADifferentTopLevelCategory()
    {
        // Lexical scoring runs on shared vocabulary alone, and operational memory reuses a lot of
        // it across subjects. Without the scope bound a preference could be folded into an
        // infrastructure note that merely talks about the same nouns.
        var store = CreateStore();
        await store.SaveAsync(Entry("infra", "Rocky prefers concise status", category: "agent-knowledge/infrastructure"));

        var match = await store.FindMostSimilarAsync(
            Entry("candidate", "Rocky prefers concise", category: "user-preferences/reporting"));

        Assert.IsNull(match);
    }

    [TestMethod]
    public async Task FindMostSimilarAsync_MatchesWithinTheSameTopLevelCategory()
    {
        // The same fact filed under sibling subcategories is the common duplicate, so the bound is
        // the top-level segment rather than the whole path.
        var store = CreateStore();
        await store.SaveAsync(Entry("existing", "Rocky prefers concise status", category: "user-preferences/status"));

        var match = await store.FindMostSimilarAsync(
            Entry("candidate", "Rocky prefers concise", category: "user-preferences/reporting"));

        Assert.IsNotNull(match);
        Assert.AreEqual("existing", match.Entry.Id);
    }

    [TestMethod]
    public async Task FindMostSimilarAsync_NullCategoryMatchesOnlyUncategorizedEntries()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("categorized", "Rocky prefers concise status", category: "user-preferences"));

        Assert.IsNull(await store.FindMostSimilarAsync(Entry("candidate", "Rocky prefers concise")));

        await store.SaveAsync(Entry("uncategorized", "Rocky prefers concise status"));

        var match = await store.FindMostSimilarAsync(Entry("candidate", "Rocky prefers concise"));
        Assert.IsNotNull(match);
        Assert.AreEqual("uncategorized", match.Entry.Id);
    }

    [TestMethod]
    public async Task FindMostSimilarAsync_IgnoresArchivedAndSupersededEntries()
    {
        // Reinforcing something already withdrawn from recall would resurrect it by the back door.
        var store = CreateStore();
        await store.SaveAsync(Entry("archived", "Rocky prefers concise status"));
        await store.SaveAsync(Entry("superseded", "Rocky prefers concise status"));
        await store.ArchiveAsync("archived", "merged");

        var superseded = (await store.GetAsync("superseded"))!;
        await store.SaveAsync(superseded with { SupersededBy = "somewhere-else" });

        Assert.IsNull(await store.FindMostSimilarAsync(Entry("candidate", "Rocky prefers concise")));
    }

    [TestMethod]
    public async Task FindMostSimilarAsync_IgnoresTheCandidateItself()
    {
        // Re-saving an existing entry must not be able to match it against itself and inflate its
        // own reinforcement count.
        var store = CreateStore();
        await store.SaveAsync(Entry("same", "Rocky prefers concise status"));

        Assert.IsNull(await store.FindMostSimilarAsync(Entry("same", "Rocky prefers concise status")));
    }

    [TestMethod]
    public async Task FindMostSimilarAsync_OnAnEmptyStore_ReturnsNull()
    {
        var store = CreateStore();

        Assert.IsNull(await store.FindMostSimilarAsync(Entry("candidate", "anything at all here")));
    }

    [TestMethod]
    public async Task FindMostSimilarAsync_WithBlankContent_ReturnsNull()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("existing", "Rocky prefers concise status"));

        Assert.IsNull(await store.FindMostSimilarAsync(Entry("candidate", "   ")));
    }

    [TestMethod]
    public void TopLevelCategory_SplitsOnTheFirstSlash()
    {
        Assert.AreEqual("user-preferences", FileMemoryStore.TopLevelCategory("user-preferences/status/daily"));
        Assert.AreEqual("general", FileMemoryStore.TopLevelCategory("general"));
        Assert.IsNull(FileMemoryStore.TopLevelCategory(null));
    }

    private FileMemoryStore CreateStore() =>
        new(Options.Create(new MemoryOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            Options.Create(new EmbeddingOptions()),
            NullLogger<FileMemoryStore>.Instance,
            EmbeddingTextPreparer.ForTests());

    private static MemoryEntry Entry(string id, string content, string? category = null) =>
        new(id, content, category, [], DateTimeOffset.UtcNow);
}
