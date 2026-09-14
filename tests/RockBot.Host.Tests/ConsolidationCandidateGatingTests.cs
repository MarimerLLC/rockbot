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
    public async Task ClusterOfEntriesAllReviewedAndUnchanged_IsNotReoffered()
    {
        // The duplicate-cluster carve-out used to re-add every clustered id unconditionally,
        // which quietly undid the reviewed-and-unchanged gate for exactly the entries most
        // likely to sit in a cluster: a pair the model has already declined to merge — or
        // whose merge the coverage check rejected — was handed back to it twice a day forever.
        // A cluster only becomes eligible when one of its members is new or edited, and every
        // member shown gets stamped on that cycle, so "all members reviewed and unchanged"
        // means "already asked, and answered".
        var store = CreateStore();
        var a = Reviewed(
            Entry("a", "Rocky has a dog named Milo the Sheltie"),
            "Rocky has a dog named Milo the Sheltie");
        var b = Reviewed(
            Entry("b", "Rocky has a Sheltie dog named Milo"),
            "Rocky has a Sheltie dog named Milo");
        await store.SaveAsync(a);
        await store.SaveAsync(b);

        var eligible = await Select(store, [a, b], threshold: 0.5);

        Assert.AreEqual(0, eligible.Count,
            "A settled duplicate cluster must stay withheld until one of its members changes.");
    }

    [TestMethod]
    public async Task SettledCluster_ReopensWhenOneMemberIsEdited()
    {
        var store = CreateStore();
        var a = Reviewed(
            Entry("a", "Rocky has a dog named Milo the Sheltie"),
            "Rocky has a dog named Milo the Sheltie");
        var b = Reviewed(
            Entry("b", "Rocky has a Sheltie dog named Milo"),
            "Rocky has a Sheltie dog named Milo");
        // Stamped against the old text, so the edit shows up as a changed fingerprint.
        var edited = b with { Content = "Rocky has a Sheltie dog named Milo, adopted in 2019" };
        await store.SaveAsync(a);
        await store.SaveAsync(edited);

        var eligible = await Select(store, [a, edited], threshold: 0.5);

        CollectionAssert.AreEquivalent(new[] { "a", "b" }, eligible.Select(e => e.Id).ToArray(),
            "One edited member has to bring its whole cluster back so the merge is still possible.");
    }

    // ── Settled-cluster reopen ───────────────────────────────────────────────

    [TestMethod]
    public async Task SettledCluster_ReviewedLongAgo_IsReopenedMergeOnly()
    {
        // Never re-asking made a single "leave them separate" answer permanent: reworded
        // duplicates of one fact stayed live for weeks with matching review stamps. Once the
        // cooldown has passed the cluster comes back — for merging, not for pruning.
        var store = CreateStore();
        var a = Reviewed(Entry("a", MiloA), MiloA, DateTimeOffset.UtcNow.AddDays(-8));
        var b = Reviewed(Entry("b", MiloB), MiloB, DateTimeOffset.UtcNow.AddDays(-8));
        await store.SaveAsync(a);
        await store.SaveAsync(b);

        var result = await SelectCandidates(store, [a, b], Lexical());

        CollectionAssert.AreEquivalent(new[] { "a", "b" }, result.Eligible.Select(e => e.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, result.MergeOnlyIds.ToArray());
        Assert.AreEqual(1, result.ReopenedClusters);
        Assert.AreEqual(1, result.OfferedClusters.Count);
    }

    [TestMethod]
    public async Task SettledCluster_WithReopenDisabled_StaysWithheld()
    {
        var store = CreateStore();
        var a = Reviewed(Entry("a", MiloA), MiloA, DateTimeOffset.UtcNow.AddDays(-60));
        var b = Reviewed(Entry("b", MiloB), MiloB, DateTimeOffset.UtcNow.AddDays(-60));
        await store.SaveAsync(a);
        await store.SaveAsync(b);

        var result = await SelectCandidates(
            store, [a, b], Lexical(o => o.SettledClusterReopenInterval = TimeSpan.Zero));

        Assert.AreEqual(0, result.Eligible.Count);
        Assert.AreEqual(0, result.ReopenedClusters);
    }

    [TestMethod]
    public async Task ClusterOpenedByANewMember_IsOfferedButNotMergeOnly()
    {
        // The existing carve-out already exposed such a cluster fully; reopen must not narrow it.
        var store = CreateStore();
        var old = Reviewed(Entry("old", MiloA), MiloA, DateTimeOffset.UtcNow.AddDays(-30));
        var fresh = Entry("new", MiloB);
        await store.SaveAsync(old);
        await store.SaveAsync(fresh);

        var result = await SelectCandidates(store, [old, fresh], Lexical());

        CollectionAssert.AreEquivalent(new[] { "old", "new" }, result.Eligible.Select(e => e.Id).ToArray());
        Assert.AreEqual(0, result.MergeOnlyIds.Count);
        Assert.AreEqual(0, result.ReopenedClusters);
        Assert.AreEqual(1, result.OfferedClusters.Count);
    }

    [TestMethod]
    public async Task ReopenedClustersAreCappedPerCycle_OldestFirst()
    {
        var store = CreateStore();
        var all = new List<MemoryEntry>();
        // No word shared across topics, so single-link clustering cannot chain them together.
        string[][] topics =
        [
            ["alpha", "bravo", "charlie", "delta"],
            ["echo", "foxtrot", "golf", "hotel"],
            ["india", "juliet", "kilo", "lima"],
            ["mike", "november", "oscar", "papa"],
        ];
        for (var i = 0; i < topics.Length; i++)
        {
            var reviewedAt = DateTimeOffset.UtcNow.AddDays(-10 - i);
            var x = string.Join(" ", topics[i]);
            var y = string.Join(" ", topics[i].Reverse());
            all.Add(Reviewed(Entry($"x{i}", x), x, reviewedAt));
            all.Add(Reviewed(Entry($"y{i}", y), y, reviewedAt));
        }
        foreach (var e in all) await store.SaveAsync(e);

        var result = await SelectCandidates(
            store, all, Lexical(o => o.SettledClusterReopenMaxPerCycle = 2), DateTimeOffset.UtcNow);

        Assert.AreEqual(2, result.ReopenedClusters);
        CollectionAssert.AreEquivalent(
            new[] { "x2", "y2", "x3", "y3" }, result.Eligible.Select(e => e.Id).ToArray(),
            "The longest-overdue clusters go first.");
    }

    [TestMethod]
    public void ReopenAt_WithoutDeclineStamps_IsOneIntervalAfterTheLatestReview()
    {
        var older = DateTimeOffset.UtcNow.AddDays(-20);
        var newer = DateTimeOffset.UtcNow.AddDays(-3);
        var members = new[]
        {
            Reviewed(Entry("a", MiloA), MiloA, older),
            Reviewed(Entry("b", MiloB), MiloB, newer),
        };

        var at = DreamService.SettledClusterReopenAt(members, new DreamOptions());

        Assert.AreEqual(newer + TimeSpan.FromDays(7), at);
    }

    [TestMethod]
    public void ReopenAt_BacksOffWithEachDecline()
    {
        var declinedAt = DateTimeOffset.UtcNow.AddDays(-10);
        var options = new DreamOptions();

        var twice = Declined(["a", "b"], declinedAt, count: 2);
        Assert.AreEqual(declinedAt + TimeSpan.FromDays(14), DreamService.SettledClusterReopenAt(twice, options));

        var once = Declined(["a", "b"], declinedAt, count: 1);
        Assert.AreEqual(declinedAt + TimeSpan.FromDays(7), DreamService.SettledClusterReopenAt(once, options));

        var many = Declined(["a", "b"], declinedAt, count: 40);
        Assert.AreEqual(declinedAt + TimeSpan.FromDays(56), DreamService.SettledClusterReopenAt(many, options),
            "The doubling is capped at the maximum interval.");
    }

    [TestMethod]
    public void ReopenAt_DeclineStampForOtherMembership_FallsBackToReviewAnchor()
    {
        // The stamp names a different set of ids — the cluster has been reshaped since — so its
        // count says nothing about this cluster.
        var reviewedAt = DateTimeOffset.UtcNow.AddDays(-2);
        var members = Declined(["a", "b"], DateTimeOffset.UtcNow.AddDays(-1), count: 3, reviewedAt: reviewedAt)
            .Select(m => m with
            {
                Metadata = new Dictionary<string, string>(m.Metadata!)
                {
                    [DreamService.ConsolidationDeclinedClusterKey] = DreamService.ClusterHash(["a", "b", "c"]),
                },
            })
            .ToArray();

        Assert.AreEqual(reviewedAt + TimeSpan.FromDays(7), DreamService.SettledClusterReopenAt(members, new DreamOptions()));
    }

    [TestMethod]
    public void ReopenAt_ReturnsNullWhenDisabled()
    {
        var members = Declined(["a", "b"], DateTimeOffset.UtcNow.AddDays(-100), count: 1);

        Assert.IsNull(DreamService.SettledClusterReopenAt(
            members, new DreamOptions { SettledClusterReopenInterval = TimeSpan.Zero }));
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
        // Bounds eligibility, not merge size: without it, single-link chaining pulls a whole
        // topic into one group. What the model then merges is constrained by the coverage
        // check, not by this cap.
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

    private static async Task<List<MemoryEntry>> Select(
        FileMemoryStore store,
        IReadOnlyList<MemoryEntry> all,
        double threshold = 0.88) =>
        (await SelectCandidates(store, all, new DreamOptions { ConsolidationSimilarityThreshold = threshold })).Eligible;

    private static Task<DreamService.ConsolidationCandidates> SelectCandidates(
        FileMemoryStore store,
        IReadOnlyList<MemoryEntry> all,
        DreamOptions options,
        DateTimeOffset? now = null) =>
        DreamService.SelectConsolidationCandidatesAsync(
            store,
            options,
            NullLogger.Instance,
            all,
            now ?? DateTimeOffset.UtcNow,
            CancellationToken.None);

    private FileMemoryStore CreateStore() =>
        new(Options.Create(new MemoryOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            Options.Create(new EmbeddingOptions()),
            NullLogger<FileMemoryStore>.Instance,
            EmbeddingTextPreparer.ForTests());

    private static MemoryEntry Entry(string id, string content) =>
        new(id, content, null, [], DateTimeOffset.UtcNow);

    private static MemoryEntry Reviewed(MemoryEntry entry, string contentAtReview, DateTimeOffset? reviewedAt = null) =>
        entry with
        {
            Metadata = new Dictionary<string, string>
            {
                [DreamService.ConsolidationReviewedHashKey] = DreamService.ContentFingerprint(contentAtReview),
                [DreamService.ConsolidationReviewedAtKey] = (reviewedAt ?? DateTimeOffset.UtcNow).ToString("O"),
            },
        };

    private const string MiloA = "Rocky has a dog named Milo the Sheltie";
    private const string MiloB = "Rocky has a Sheltie dog named Milo";

    // Lexical fallback (no embedding generator in tests), so the threshold is Jaccard-scale.
    private static DreamOptions Lexical(Action<DreamOptions>? configure = null)
    {
        var options = new DreamOptions { ConsolidationSimilarityThreshold = 0.5 };
        configure?.Invoke(options);
        return options;
    }

    private static MemoryEntry[] Declined(
        string[] ids, DateTimeOffset declinedAt, int count, DateTimeOffset? reviewedAt = null) =>
        [.. ids.Select(id =>
        {
            var reviewed = Reviewed(Entry(id, $"fact {id}"), $"fact {id}", reviewedAt);
            return reviewed with
            {
                Metadata = new Dictionary<string, string>(reviewed.Metadata!)
                {
                    [DreamService.ConsolidationDeclinedClusterKey] = DreamService.ClusterHash(ids),
                    [DreamService.ConsolidationDeclinedAtKey] = declinedAt.ToString("O"),
                    [DreamService.ConsolidationDeclinedCountKey] = count.ToString(),
                },
            };
        })];
}
