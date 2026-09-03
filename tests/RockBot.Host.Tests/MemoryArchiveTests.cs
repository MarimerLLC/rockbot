using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers the archive tier — the recovery path for automated removals.
/// </summary>
/// <remarks>
/// Consolidation used to hard-delete, so a bad merge destroyed the only copy of a fact. A
/// live corpus lost 26% of its entries in 3.5 days that way, including entries reinforced
/// 214× and scored 0.99 importance. Archiving is what makes a wrong call cost recall until
/// someone notices, rather than the fact itself.
/// </remarks>
[TestClass]
public class MemoryArchiveTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup() =>
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-archive-test-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task ArchivedEntry_IsHiddenFromSearch_ButStillRetrievableById()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("keep", "Rocky has a Sheltie named Milo"));
        await store.SaveAsync(Entry("gone", "Trish Roberts is a collaborator at Xebia"));

        await store.ArchiveAsync("gone", "merged into keep");

        var visible = await store.SearchAsync(new MemorySearchCriteria(MaxResults: 50));
        CollectionAssert.AreEquivalent(new[] { "keep" }, visible.Select(e => e.Id).ToArray());

        // The whole point: still recoverable.
        var archived = await store.GetAsync("gone");
        Assert.IsNotNull(archived);
        Assert.AreEqual("Trish Roberts is a collaborator at Xebia", archived.Content);
        Assert.IsNotNull(archived.ArchivedAt);
        Assert.AreEqual("merged into keep", archived.ArchiveReason);
    }

    [TestMethod]
    public async Task ArchivedEntry_IsVisibleWhenExplicitlyRequested()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("gone", "Rockford Duane Lhotka appears in travel data"));
        await store.ArchiveAsync("gone", "flagged ephemeral by consolidation");

        var audited = await store.SearchAsync(new MemorySearchCriteria(MaxResults: 50, IncludeArchived: true));

        Assert.AreEqual(1, audited.Count);
        Assert.AreEqual("gone", audited[0].Id);
    }

    [TestMethod]
    public async Task ArchivedEntry_SurvivesStoreRestart()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("gone", "PWOP Productions W-9 was completed"));
        await store.ArchiveAsync("gone", "merged into abc123");

        // Fresh index built from disk — archival must be persisted, not just in-memory state.
        var reopened = CreateStore();

        Assert.AreEqual(0, (await reopened.SearchAsync(new MemorySearchCriteria(MaxResults: 50))).Count);
        var recovered = await reopened.GetAsync("gone");
        Assert.IsNotNull(recovered);
        Assert.AreEqual("merged into abc123", recovered.ArchiveReason);
    }

    [TestMethod]
    public async Task RestoreAsync_ReturnsEntryToRecall()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("gone", "Rocky's default timezone is America/Chicago"));
        await store.ArchiveAsync("gone", "merged into xyz");

        Assert.IsTrue(await store.RestoreAsync("gone"));

        var visible = await store.SearchAsync(new MemorySearchCriteria(MaxResults: 50));
        Assert.AreEqual(1, visible.Count);
        Assert.IsNull(visible[0].ArchivedAt);
        Assert.IsNull(visible[0].ArchiveReason);
    }

    [TestMethod]
    public async Task RestoreAsync_ReturnsFalseForEntryThatWasNeverArchived()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("live", "still here"));

        Assert.IsFalse(await store.RestoreAsync("live"));
        Assert.IsFalse(await store.RestoreAsync("no-such-id"));
    }

    [TestMethod]
    public async Task ArchiveAsync_IsIdempotent_AndPreservesTheOriginalReason()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("gone", "content"));

        await store.ArchiveAsync("gone", "first reason");
        var firstStamp = (await store.GetAsync("gone"))!.ArchivedAt;

        await store.ArchiveAsync("gone", "second reason");
        var after = await store.GetAsync("gone");

        Assert.AreEqual("first reason", after!.ArchiveReason);
        Assert.AreEqual(firstStamp, after.ArchivedAt);
    }

    [TestMethod]
    public async Task ArchiveAsync_OnMissingEntry_IsANoOp()
    {
        var store = CreateStore();
        await store.ArchiveAsync("never-existed", "reason");
        Assert.IsNull(await store.GetAsync("never-existed"));
    }

    [TestMethod]
    public async Task PurgeArchivedAsync_DeletesOnlyEntriesPastRetention()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("old", "archived long ago"));
        await store.SaveAsync(Entry("recent", "archived just now"));
        await store.SaveAsync(Entry("live", "never archived"));

        await store.ArchiveAsync("old", "merged");
        await store.ArchiveAsync("recent", "merged");

        // Backdate one archive stamp past the retention window.
        var old = (await store.GetAsync("old"))!;
        await store.SaveAsync(old with { ArchivedAt = DateTimeOffset.UtcNow.AddDays(-120) });

        var result = await store.PurgeArchivedAsync(TimeSpan.FromDays(90));

        Assert.AreEqual(1, result.Purged);
        Assert.AreEqual(0, result.Kept);
        Assert.IsNull(await store.GetAsync("old"));
        Assert.IsNotNull(await store.GetAsync("recent"));
        Assert.IsNotNull(await store.GetAsync("live"));
    }

    [TestMethod]
    public async Task PurgeArchivedAsync_KeepsEntriesAboveTheFloor()
    {
        // The purge is the last place that destroys memory, so the floor that stops consolidation
        // pruning a high-value entry applies here too. Importance is frozen at archive time and
        // never decays afterwards, so in practice it is the reinforcement half that bites.
        var store = CreateStore();
        await store.SaveAsync(Entry("valuable", "seen over and over") with { ReinforcementCount = 5 });
        await store.SaveAsync(Entry("ordinary", "seen once") with { ReinforcementCount = 1 });

        await store.ArchiveAsync("valuable", "merged");
        await store.ArchiveAsync("ordinary", "merged");

        foreach (var id in new[] { "valuable", "ordinary" })
        {
            var archived = (await store.GetAsync(id))!;
            await store.SaveAsync(archived with { ArchivedAt = DateTimeOffset.UtcNow.AddDays(-120) });
        }

        var options = new DreamOptions();
        var result = await store.PurgeArchivedAsync(
            TimeSpan.FromDays(90), e => DreamService.IsProtectedFromPruning(e, options));

        Assert.AreEqual(1, result.Purged);
        Assert.AreEqual(1, result.Kept);
        Assert.IsNotNull(await store.GetAsync("valuable"));
        Assert.IsNull(await store.GetAsync("ordinary"));
    }

    [TestMethod]
    public async Task PurgeArchivedAsync_WithNonPositiveRetention_KeepsEverythingForever()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("old", "archived long ago"));
        await store.ArchiveAsync("old", "merged");
        var old = (await store.GetAsync("old"))!;
        await store.SaveAsync(old with { ArchivedAt = DateTimeOffset.UtcNow.AddYears(-5) });

        Assert.AreEqual(0, (await store.PurgeArchivedAsync(TimeSpan.Zero)).Purged);
        Assert.IsNotNull(await store.GetAsync("old"));
    }

    [TestMethod]
    public async Task ArchivedEntries_AreExcludedFromTagAndCategoryVocabulary()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("live", "visible", category: "work/live", tags: ["kept"]));
        await store.SaveAsync(Entry("gone", "hidden", category: "work/colleagues", tags: ["dropped"]));

        await store.ArchiveAsync("gone", "merged");

        CollectionAssert.AreEquivalent(new[] { "kept" }, (await store.ListTagsAsync()).ToArray());
        CollectionAssert.AreEquivalent(new[] { "work/live" }, (await store.ListCategoriesAsync()).ToArray());
    }

    private FileMemoryStore CreateStore() =>
        new(Options.Create(new MemoryOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            Options.Create(new EmbeddingOptions()),
            NullLogger<FileMemoryStore>.Instance,
            EmbeddingTextPreparer.ForTests());

    private static MemoryEntry Entry(
        string id,
        string content,
        string? category = null,
        string[]? tags = null) =>
        new(id, content, category, tags ?? [], DateTimeOffset.UtcNow);
}
