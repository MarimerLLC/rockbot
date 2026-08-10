using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers the gate that decides which entries dream consolidation is allowed to touch.
/// </summary>
/// <remarks>
/// Handing the LLM the whole corpus every cycle means every entry is re-tried for deletion
/// every cycle, and survival compounds: at the default twice-daily cadence a one-in-a-thousand
/// misjudgement per entry per cycle loses about half the corpus in a year. Gating converts an
/// unbounded repeated gamble into a decision taken once per entry per content change, which is
/// why "already reviewed and unchanged is withheld" is the load-bearing assertion here.
/// </remarks>
[TestClass]
public class ConsolidationCandidateGatingTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init() =>
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-gating-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Review stamps ────────────────────────────────────────────────────────

    [TestMethod]
    public void UnstampedEntry_IsEligible()
    {
        Assert.IsFalse(DreamService.IsReviewedAndUnchanged(Entry("a", "Rocky has a dog named Milo")));
    }

    [TestMethod]
    public void StampedEntry_WithUnchangedContent_IsWithheld()
    {
        var content = "Rocky has a dog named Milo";
        Assert.IsTrue(DreamService.IsReviewedAndUnchanged(Reviewed(Entry("a", content), content)));
    }

    [TestMethod]
    public void StampedEntry_BecomesEligibleAgainWhenContentChanges()
    {
        // Any edit path counts — reinforcement, a tool update, a previous merge. The stamp is
        // a content fingerprint precisely so no write path can bypass it.
        var stamped = Reviewed(Entry("a", "Rocky has a dog named Milo"), "Rocky has a dog named Milo");
        var edited = stamped with { Content = "Rocky has a Sheltie named Milo" };

        Assert.IsFalse(DreamService.IsReviewedAndUnchanged(edited));
    }

    [TestMethod]
    public void StampWithAForeignHash_IsTreatedAsUnreviewed()
    {
        var entry = Entry("a", "content") with
        {
            Metadata = new Dictionary<string, string>
            {
                [DreamService.ConsolidationReviewedHashKey] = "DEADBEEFDEADBEEF",
            },
        };

        Assert.IsFalse(DreamService.IsReviewedAndUnchanged(entry));
    }

    // ── Selection ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ReviewedUnchangedEntries_AreWithheldFromTheLlm()
    {
        // Three unrelated facts, all previously reviewed. Nothing changed, nothing is a
        // duplicate — so consolidation gets to see none of them and can delete none of them.
        var store = CreateStore();
        var all = new[]
        {
            Reviewed(Entry("a", "Rocky lives in Minnesota"), "Rocky lives in Minnesota"),
            Reviewed(Entry("b", "Trish Roberts is a Xebia collaborator"), "Trish Roberts is a Xebia collaborator"),
            Reviewed(Entry("c", "The estimated tax deadline is September 10"), "The estimated tax deadline is September 10"),
        };
        foreach (var e in all) await store.SaveAsync(e);

        var eligible = await Select(store, all);

        Assert.AreEqual(0, eligible.Count);
    }

    [TestMethod]
    public async Task NewOrChangedEntries_AreAlwaysEligible()
    {
        var store = CreateStore();
        var reviewed = Reviewed(Entry("old", "Rocky lives in Minnesota"), "Rocky lives in Minnesota");
        var fresh = Entry("new", "Rocky is speaking at VSLive Las Vegas");
        await store.SaveAsync(reviewed);
        await store.SaveAsync(fresh);

        var eligible = await Select(store, [reviewed, fresh]);

        CollectionAssert.AreEquivalent(new[] { "new" }, eligible.Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public async Task ReviewedEntry_IsPulledBackInWhenSomethingDuplicatesIt()
    {
        // A fresh entry restating an old reviewed one has to be mergeable, which means the
        // old one must be visible again — otherwise duplicates could never be collapsed.
        var store = CreateStore();
        var reviewed = Reviewed(
            Entry("old", "Rocky has a dog named Milo the Sheltie"),
            "Rocky has a dog named Milo the Sheltie");
        var fresh = Entry("new", "Rocky has a Sheltie dog named Milo");
        await store.SaveAsync(reviewed);
        await store.SaveAsync(fresh);

        // Lexical fallback (no embedding generator in tests), so use a threshold that matches
        // Jaccard overlap rather than a cosine-scale one.
        var eligible = await Select(store, [reviewed, fresh], threshold: 0.5);

        CollectionAssert.AreEquivalent(new[] { "old", "new" }, eligible.Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public async Task SelectionPreservesStoreOrdering()
    {
        var store = CreateStore();
        var all = new[] { Entry("a", "first fact"), Entry("b", "second fact"), Entry("c", "third fact") };
        foreach (var e in all) await store.SaveAsync(e);

        var eligible = await Select(store, all);

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, eligible.Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public void ReviewStamp_IsIndependentOfImportanceAndTimestamps()
    {
        // The stamp keys off content only. Importance decay rewrites ImportanceScore and
        // UpdatedAt on every cycle; if either fed the stamp, decayed entries would look
        // "changed" forever and the gate would leak the whole corpus straight back through.
        var content = "Rocky lives in Minnesota";
        var stamped = Reviewed(Entry("a", content), content);

        var decayed = stamped with
        {
            ImportanceScore = 0.1f,
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(1),
            LastSeenAt = DateTimeOffset.UtcNow.AddDays(1),
            ReinforcementCount = 99,
        };

        Assert.IsTrue(DreamService.IsReviewedAndUnchanged(decayed));
    }

    // ── Clustering ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task NearDuplicates_Cluster_AndUnrelatedEntriesDoNot()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("dup1", "Rocky has a dog named Milo the Sheltie"));
        await store.SaveAsync(Entry("dup2", "Rocky has a Sheltie dog named Milo"));
        await store.SaveAsync(Entry("other", "Estimated quarterly taxes are due September 10"));

        var clusters = await store.FindNearDuplicateClustersAsync(0.5, 3);

        Assert.AreEqual(1, clusters.Count);
        CollectionAssert.AreEquivalent(new[] { "dup1", "dup2" }, clusters[0].ToArray());
    }

    [TestMethod]
    public async Task ThresholdControlsHowMuchIsExposed()
    {
        // Partial overlap: related enough to merge under a permissive threshold, not enough
        // under a strict one. The threshold is the main dial on how much consolidation may
        // touch, so it has to actually bite in both directions.
        // Tokens are {rocky, lives, minnesota} vs {rocky, lives, minneapolis, minnesota},
        // so Jaccard is 3/4 — comfortably inside 0.50 and comfortably outside 0.99.
        var store = CreateStore();
        await store.SaveAsync(Entry("a", "Rocky lives in Minnesota"));
        await store.SaveAsync(Entry("b", "Rocky lives in Minneapolis Minnesota"));

        Assert.AreEqual(0, (await store.FindNearDuplicateClustersAsync(0.99, 3)).Count);
        Assert.AreEqual(1, (await store.FindNearDuplicateClustersAsync(0.50, 3)).Count);
    }

    [TestMethod]
    public async Task ClustersAreSplitAtMaxClusterSize()
    {
        // Caps merge fan-in: without it, single-link chaining lets one cluster swallow a whole
        // topic and invites the model to collapse it into a single entry.
        var store = CreateStore();
        for (var i = 0; i < 5; i++)
            await store.SaveAsync(Entry($"dup{i}", "Rocky has a Sheltie dog named Milo"));

        var clusters = await store.FindNearDuplicateClustersAsync(0.5, 2);

        Assert.IsTrue(clusters.All(c => c.Count <= 2), "no cluster may exceed the cap");
        Assert.AreEqual(4, clusters.Sum(c => c.Count), "the odd one out has nothing to merge with");
    }

    [TestMethod]
    public async Task ArchivedEntries_AreNotOfferedAsDuplicateCandidates()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("live", "Rocky has a Sheltie dog named Milo"));
        await store.SaveAsync(Entry("archived", "Rocky has a dog named Milo the Sheltie"));
        await store.ArchiveAsync("archived", "merged earlier");

        Assert.AreEqual(0, (await store.FindNearDuplicateClustersAsync(0.5, 3)).Count);
    }

    [TestMethod]
    public async Task MaxClusterSizeBelowTwo_DisablesClustering()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("dup1", "Rocky has a Sheltie dog named Milo"));
        await store.SaveAsync(Entry("dup2", "Rocky has a Sheltie dog named Milo"));

        Assert.AreEqual(0, (await store.FindNearDuplicateClustersAsync(0.5, 1)).Count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Task<List<MemoryEntry>> Select(
        FileMemoryStore store,
        IReadOnlyList<MemoryEntry> all,
        double threshold = 0.88) =>
        DreamService.SelectConsolidationCandidatesAsync(
            store,
            new DreamOptions { ConsolidationSimilarityThreshold = threshold },
            NullLogger.Instance,
            all,
            CancellationToken.None);

    private FileMemoryStore CreateStore() =>
        new(Options.Create(new MemoryOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            Options.Create(new EmbeddingOptions()),
            NullLogger<FileMemoryStore>.Instance,
            EmbeddingTextPreparer.ForTests());

    private static MemoryEntry Entry(string id, string content) =>
        new(id, content, null, [], DateTimeOffset.UtcNow);

    private static MemoryEntry Reviewed(MemoryEntry entry, string contentAtReview) =>
        entry with
        {
            Metadata = new Dictionary<string, string>
            {
                [DreamService.ConsolidationReviewedHashKey] = DreamService.ContentFingerprint(contentAtReview),
                [DreamService.ConsolidationReviewedAtKey] = DateTimeOffset.UtcNow.ToString("O"),
            },
        };
}
