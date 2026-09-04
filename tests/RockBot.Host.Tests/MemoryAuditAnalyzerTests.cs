namespace RockBot.Host.Tests;

/// <summary>
/// The analyzer is pure, which is the point: the August 2026 data-loss incident can be replayed
/// here as plain data and the audit must report it, with nothing mocked and no clock involved.
/// </summary>
[TestClass]
public class MemoryAuditAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Yesterday = Now.AddDays(-1);

    [TestMethod]
    public void FirstRun_HasNoDeltasAndNoPreviousTimestamp()
    {
        var entries = new List<MemoryEntry> { Entry("a"), Entry("b"), Entry("c") };

        var (snapshot, state) = Analyze(entries, previous: null);

        Assert.IsNull(snapshot.PreviousTakenAt);
        Assert.AreEqual(3, snapshot.Live);
        Assert.AreEqual(0, snapshot.CreatedSinceLast, "A first run has nothing to compare against.");
        Assert.AreEqual(0, snapshot.HardDeletedSinceLast);
        Assert.IsNull(snapshot.NetGrowthPerDay,
            "With no previous run there is no window, so the rate is unmeasurable rather than zero.");
        Assert.AreEqual(MemoryAuditStatuses.Healthy, snapshot.Status);
        Assert.AreEqual(3, state.Entries.Count);
    }

    [TestMethod]
    public void TheAugustScenario_ReportsSeventyFourHardDeletesAndAlerts()
    {
        // 148 live entries, then 109 — 74 ids gone outright, 35 new ones created in the same
        // window. None of the 74 was archived first, so the retention purge cannot explain any
        // of them.
        var survivors = Enumerable.Range(0, 74).Select(i => Entry($"kept{i}")).ToList();
        var fresh = Enumerable.Range(0, 35).Select(i => Entry($"new{i}")).ToList();
        var now = survivors.Concat(fresh).ToList();

        var previousRows = survivors
            .Select(e => Row(e.Id))
            .Concat(Enumerable.Range(0, 74).Select(i => Row($"lost{i}")))
            .ToList();

        var (snapshot, _) = Analyze(now, PreviousState(previousRows));

        Assert.AreEqual(109, snapshot.Live);
        Assert.AreEqual(74, snapshot.HardDeletedSinceLast);
        Assert.AreEqual(0, snapshot.PurgedSinceLast, "Nothing was archived, so nothing was purge-eligible.");
        Assert.AreEqual(74, snapshot.HardDeletedOutsidePurge);
        Assert.AreEqual(35, snapshot.CreatedSinceLast);
        Assert.AreEqual(MemoryAuditStatuses.Alert, snapshot.Status);

        Assert.IsTrue(
            snapshot.Invariants.Any(v => v.Name == MemoryAuditInvariants.NoHardDeleteOutsidePurge),
            "A hard delete the purge cannot account for is the finding the audit exists for.");
        Assert.IsTrue(
            snapshot.Invariants.Any(v => v.Name == MemoryAuditInvariants.LossPercentThreshold),
            "148 → 109 is a 26% drop, well past the 10% default.");
    }

    [TestMethod]
    public void PurgeEligibleIdsAreCountedAsPurged_NotAsHardDeletes()
    {
        var previous = PreviousState(
        [
            // Archived 100 days ago; the 90-day retention window has closed.
            Row("aged", archived: true, archivedAt: Now.AddDays(-100)),
            // Archived yesterday; far too recent for the purge to have taken it.
            Row("recent", archived: true, archivedAt: Now.AddDays(-1))
        ]);

        var (snapshot, _) = Analyze([], previous);

        Assert.AreEqual(2, snapshot.HardDeletedSinceLast);
        Assert.AreEqual(1, snapshot.PurgedSinceLast);
        Assert.AreEqual(1, snapshot.HardDeletedOutsidePurge);
    }

    [TestMethod]
    public void ChainDepthHistogramCountsEachLiveDepth()
    {
        // raw → merge1 (depth 1) → merge2 (depth 2) → merge3 (depth 3)
        var raw = Archived("raw", "merged into merge1");
        var merge1 = Archived("merge1", "merged into merge2") with { Metadata = MergedFrom("raw") };
        var merge2 = Archived("merge2", "merged into merge3") with { Metadata = MergedFrom("merge1") };
        var merge3 = Entry("merge3") with { Metadata = MergedFrom("merge2") };

        // An independent single-hop merge, so depth 1 has a live representative too.
        var srcA = Archived("srcA", "merged into flat");
        var flat = Entry("flat") with { Metadata = MergedFrom("srcA") };

        var (snapshot, _) = Analyze([raw, merge1, merge2, merge3, srcA, flat], previous: null);

        Assert.AreEqual(3, snapshot.MaxChainDepth);
        Assert.AreEqual(1, snapshot.MergeChainDepth["1"], "flat");
        Assert.AreEqual(1, snapshot.MergeChainDepth["3"], "merge3");
        Assert.IsFalse(snapshot.MergeChainDepth.ContainsKey("2"),
            "merge2 is archived, so it does not appear in the live histogram.");

        Assert.IsTrue(snapshot.Invariants.Any(v => v.Name == MemoryAuditInvariants.ChainDepthThreshold));
    }

    [TestMethod]
    public void TheChainDepthHistogramReadsInDepthOrder()
    {
        // A live corpus produced "1: 78, 3: 13, 4: 3, 2: 21, 7: 3, 5: 1, 6: 1, 8: 1" — every
        // count correct and unreadable. Insertion order is what both the report and the raw
        // snapshot row show.
        var entries = new List<MemoryEntry>();
        string? previousId = null;

        // One chain d0→d4, all live, giving depths 0 through 4.
        for (var depth = 0; depth <= 4; depth++)
        {
            var id = $"d{depth}";
            entries.Add(previousId is null ? Entry(id) : Entry(id) with { Metadata = MergedFrom(previousId) });
            previousId = id;
        }

        // Two more live merges appended so the deeper depth is *encountered first*. Without an
        // explicit sort the dictionary would then enumerate 1, 2, 3, 4, 3, 1 in insertion order.
        entries.Add(Entry("x3") with { Metadata = MergedFrom("d2") });
        entries.Add(Entry("x1") with { Metadata = MergedFrom("d0") });

        var (snapshot, _) = Analyze(entries, previous: null);

        var keys = snapshot.MergeChainDepth.Keys.Select(int.Parse).ToList();
        CollectionAssert.AreEqual(
            keys.OrderBy(k => k).ToList(), keys,
            "The histogram must enumerate in ascending depth order.");
    }

    [TestMethod]
    public void AMergeWhoseSourcesWerePurgedStillCountsAsDepthOne()
    {
        var merge = Entry("merge") with { Metadata = MergedFrom("long-gone", "also-gone") };

        var (snapshot, _) = Analyze([merge], previous: null);

        Assert.AreEqual(1, snapshot.MaxChainDepth);
    }

    [TestMethod]
    public void ReinforcedWithoutMerge_CountsOnlyRealReObservation()
    {
        var reObserved = Entry("a") with { ReinforcementCount = 7 };
        var merged = Entry("b") with { ReinforcementCount = 9, Metadata = MergedFrom("x", "y") };
        var untouched = Entry("c") with { ReinforcementCount = 3 };

        var previous = PreviousState(
        [
            Row("a", reinforcementCount: 5),
            // Its reinforcement count rose because a merge summed its sources, not because
            // anything was observed again.
            Row("b", reinforcementCount: 4),
            Row("c", reinforcementCount: 3)
        ]);

        var (snapshot, _) = Analyze([reObserved, merged, untouched], previous);

        Assert.AreEqual(1, snapshot.ReinforcedWithoutMergeSinceLast);
    }

    [TestMethod]
    public void PurgeOutlookSeparatesTheHighValueEntriesTheFloorWillKeep()
    {
        var options = new DreamOptions();

        // 90-day retention, 7-day warning window: archived 85 days ago is due within a week.
        var ordinary = Archived("ordinary", "ephemeral", Now.AddDays(-85));
        var precious = Archived("precious", "ephemeral", Now.AddDays(-85))
            with { ReinforcementCount = options.PruningProtectionReinforcementCount + 10 };
        var notYet = Archived("notYet", "ephemeral", Now.AddDays(-10));

        var (snapshot, _) = Analyze([ordinary, precious, notYet], previous: null);

        Assert.AreEqual(7, snapshot.Purge.DueWithinDays);
        Assert.AreEqual(2, snapshot.Purge.Count);
        Assert.AreEqual(1, snapshot.Purge.HighValueCount);
    }

    [TestMethod]
    public void TopCategoriesByGrowth_RanksByAbsoluteNetMovement()
    {
        var entries = new List<MemoryEntry>
        {
            Entry("n1", "project"), Entry("n2", "project"), Entry("n3", "project"),
            Entry("n4", "personal"),
            Archived("old1", "ephemeral") with { Category = "chores" },
            Archived("old2", "ephemeral") with { Category = "chores" }
        };

        var previous = PreviousState(
        [
            Row("old1", category: "chores"),
            Row("old2", category: "chores")
        ]);

        var (snapshot, _) = Analyze(entries, previous);

        var project = snapshot.TopCategoriesByGrowth.Single(c => c.Category == "project");
        Assert.AreEqual(3, project.Created);
        Assert.AreEqual(3, project.Net);

        var chores = snapshot.TopCategoriesByGrowth.Single(c => c.Category == "chores");
        Assert.AreEqual(2, chores.Archived);
        Assert.AreEqual(-2, chores.Net);

        Assert.AreEqual("project", snapshot.TopCategoriesByGrowth[0].Category);
    }

    [TestMethod]
    public void NetGrowthPerDay_IsPerDayNotPerRun()
    {
        var entries = Enumerable.Range(0, 20).Select(i => Entry($"e{i}")).ToList();
        var previous = PreviousState(
            [.. Enumerable.Range(0, 10).Select(i => Row($"e{i}"))],
            takenAt: Now.AddDays(-2));

        var (snapshot, _) = Analyze(entries, previous);

        Assert.AreEqual(5.0, snapshot.NetGrowthPerDay!.Value, 1e-9, "10 new entries over 2 days.");
    }

    [TestMethod]
    public void AWindowTooShortForARate_ReportsItAsUnmeasurableRatherThanExtrapolating()
    {
        // Observed live: a restart put two runs seven minutes apart, and six ordinary saves
        // annualized to 1311 entries/day — tripping the growth threshold on every deploy.
        var entries = Enumerable.Range(0, 16).Select(i => Entry($"e{i}")).ToList();
        var previous = PreviousState(
            [.. Enumerable.Range(0, 10).Select(i => Row($"e{i}"))],
            takenAt: Now.AddMinutes(-7));

        var (snapshot, _) = Analyze(entries, previous);

        Assert.IsNull(snapshot.NetGrowthPerDay,
            "A seven-minute window cannot measure a daily rate.");
        Assert.AreEqual(6, snapshot.CreatedSinceLast,
            "The absolute counts are still exact — only the rate is withheld.");
        Assert.IsFalse(
            snapshot.Invariants.Any(v => v.Name == MemoryAuditInvariants.NetGrowthThreshold),
            "A rate that was never measured must not trip a rate threshold.");
    }

    [TestMethod]
    public void AShortWindowStillReportsRealLoss()
    {
        // The short-window guard must suppress only rates, never the loss findings — those are
        // absolute counts and are exactly what a restart-triggered run should still catch.
        var previous = PreviousState([Row("a"), Row("b"), Row("c")], takenAt: Now.AddMinutes(-7));

        var (snapshot, _) = Analyze([Entry("a")], previous);

        Assert.IsNull(snapshot.NetGrowthPerDay);
        Assert.AreEqual(2, snapshot.HardDeletedOutsidePurge);
        Assert.AreEqual(MemoryAuditStatuses.Alert, snapshot.Status);
    }

    [TestMethod]
    public void RestartsAreCountedFromTheStartTimesSinceTheLastRun()
    {
        var previous = PreviousState([Row("a")]);

        var (snapshot, state) = Analyze(
            [Entry("a")],
            previous,
            processStarts: [Now.AddDays(-3), Now.AddHours(-6), Now.AddHours(-2)]);

        Assert.AreEqual(2, snapshot.RestartsSinceLast, "Only the two starts after yesterday count.");
        Assert.AreEqual(3, state.ProcessStarts.Count, "All three are still inside the 60-day window.");
    }

    [TestMethod]
    public void RepeatedRejectionsAreTrackedAcrossRunsAndReported()
    {
        var rejected = Entry("a") with
        {
            Metadata = new Dictionary<string, string>
            {
                [DreamService.ConsolidationRejectedClusterKey] = "CLUSTER1",
                [DreamService.ConsolidationRejectedAtKey] = Now.AddHours(-2).ToString("O")
            }
        };

        // The same cluster has already been rejected on the two previous runs.
        var previous = PreviousState([Row("a")]) with
        {
            RejectedClusterRuns = new Dictionary<string, int> { ["CLUSTER1"] = 2 }
        };

        var (snapshot, state) = Analyze([rejected], previous);

        Assert.AreEqual(1, snapshot.RejectedMergeSourcesSinceLast);
        Assert.AreEqual(3, state.RejectedClusterRuns["CLUSTER1"]);
        Assert.AreEqual(1, snapshot.RejectedMergeClustersRepeated);
        Assert.IsTrue(snapshot.Invariants.Any(v => v.Name == MemoryAuditInvariants.NoRepeatedRejection));
    }

    [TestMethod]
    public void AClusterThatStopsBeingRejectedDropsOutRatherThanDecaying()
    {
        var previous = PreviousState([Row("a")]) with
        {
            RejectedClusterRuns = new Dictionary<string, int> { ["CLUSTER1"] = 2 }
        };

        var (snapshot, state) = Analyze([Entry("a")], previous);

        Assert.AreEqual(0, state.RejectedClusterRuns.Count);
        Assert.AreEqual(0, snapshot.RejectedMergeClustersRepeated);
    }

    [TestMethod]
    public void AStaleRejectionStampFromBeforeTheLastRunIsNotCountedAgain()
    {
        var rejected = Entry("a") with
        {
            Metadata = new Dictionary<string, string>
            {
                [DreamService.ConsolidationRejectedClusterKey] = "CLUSTER1",
                // Stamped before the previous audit run, which already reported it.
                [DreamService.ConsolidationRejectedAtKey] = Now.AddDays(-5).ToString("O")
            }
        };

        var (snapshot, _) = Analyze([rejected], PreviousState([Row("a")]));

        Assert.AreEqual(0, snapshot.RejectedMergeSourcesSinceLast);
    }

    [TestMethod]
    public void DreamPassCadenceComesFromTheLedger()
    {
        var ledger = new Dictionary<string, DateTimeOffset>
        {
            ["memory consolidation"] = Now.AddHours(-8),
            ["skill consolidation"] = Now.AddHours(-8),
            ["identity reflection"] = Now.AddDays(-4)  // before the previous run
        };

        var (snapshot, _) = Analyze([Entry("a")], PreviousState([Row("a")]), ledger: ledger);

        Assert.AreEqual(2, snapshot.DreamPassesRunSinceLast);
        Assert.AreEqual(Now.AddHours(-8), snapshot.ConsolidationLastRunAt);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (MemoryAuditSnapshot Snapshot, MemoryAuditState State) Analyze(
        IReadOnlyList<MemoryEntry> entries,
        MemoryAuditState? previous,
        IReadOnlyList<DateTimeOffset>? processStarts = null,
        IReadOnlyDictionary<string, DateTimeOffset>? ledger = null,
        MemoryAuditOptions? options = null) =>
        MemoryAuditAnalyzer.Analyze(
            new MemoryStoreWalker.WalkResult(entries, EmptyCategoryDirs: 0, MalformedFiles: 0),
            previous,
            ledger ?? new Dictionary<string, DateTimeOffset>(),
            processStarts ?? [],
            embeddingDupClusters: null,
            vocabularyStoplistSize: 0,
            eval: null,
            new DreamOptions(),
            options ?? new MemoryAuditOptions(),
            Now,
            "snap1");

    private static MemoryAuditState PreviousState(
        IReadOnlyList<MemoryAuditEntryRow> rows, DateTimeOffset? takenAt = null) =>
        new() { TakenAt = takenAt ?? Yesterday, SnapshotId = "snap0", Entries = rows };

    private static MemoryAuditEntryRow Row(
        string id,
        bool archived = false,
        DateTimeOffset? archivedAt = null,
        int reinforcementCount = 1,
        int mergedFromCount = 0,
        string? category = null) =>
        new(id, archived, archivedAt, reinforcementCount, mergedFromCount, category);

    private static MemoryEntry Entry(string id, string? category = null) =>
        new(id, $"content for {id}", category, [], Now.AddDays(-30));

    private static MemoryEntry Archived(string id, string reason, DateTimeOffset? at = null) =>
        Entry(id) with { ArchivedAt = at ?? Now.AddDays(-2), ArchiveReason = reason };

    private static Dictionary<string, string> MergedFrom(params string[] ids) => new()
    {
        [DreamService.MergedFromKey] = string.Join(",", ids),
        [DreamService.MergedAtKey] = Now.AddDays(-1).ToString("O")
    };
}
