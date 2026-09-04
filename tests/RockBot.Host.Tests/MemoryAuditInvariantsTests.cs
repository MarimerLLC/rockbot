namespace RockBot.Host.Tests;

/// <summary>
/// One test per invariant, plus the status ladder. These are the statements the store is
/// supposed to make true; the audit is only worth having if each one actually fires.
/// </summary>
[TestClass]
public class MemoryAuditInvariantsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AHealthyCorpusProducesNoViolations()
    {
        var source = Archived("src", $"{DreamService.MergedIntoReasonPrefix}merged");
        var merged = Entry("merged") with { Metadata = MergedFrom("src") };

        var violations = Check([source, merged], Snapshot());

        Assert.AreEqual(0, violations.Count, string.Join("; ", violations.Select(v => v.Name)));
        Assert.AreEqual(MemoryAuditStatuses.Healthy, MemoryAuditInvariants.ComputeStatus(violations));
    }

    [TestMethod]
    public void MergeChainUnbroken_FiresOnTheIssue506Shape()
    {
        // Sources archived "merged into X" where X exists nowhere: the fact has no surviving copy.
        var a = Archived("a", $"{DreamService.MergedIntoReasonPrefix}vanished");
        var b = Archived("b", $"{DreamService.MergedIntoReasonPrefix}vanished");

        var violations = Check([a, b], Snapshot());

        var violation = violations.Single(v => v.Name == MemoryAuditInvariants.MergeChainUnbroken);
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, violation.Ids.ToArray());
        Assert.AreEqual(MemoryAuditStatuses.Alert, MemoryAuditInvariants.ComputeStatus(violations));
    }

    [TestMethod]
    public void MergedFromResolves_FiresWhenARecentMergeNamesAMissingSource()
    {
        var merged = Entry("merged") with { Metadata = MergedFrom("never-existed") };

        var violations = Check([merged], Snapshot());

        Assert.IsTrue(violations.Any(v => v.Name == MemoryAuditInvariants.MergedFromResolves));
    }

    [TestMethod]
    public void MergedFromResolves_ToleratesProvenanceThePurgeHasAgedOut()
    {
        // Provenance dangling after the retention window is documented behaviour, not a fault.
        var merged = Entry("merged") with
        {
            Metadata = new Dictionary<string, string>
            {
                [DreamService.MergedFromKey] = "long-purged",
                [DreamService.MergedAtKey] = Now.AddDays(-200).ToString("O")
            }
        };

        var violations = Check([merged], Snapshot());

        Assert.IsFalse(violations.Any(v => v.Name == MemoryAuditInvariants.MergedFromResolves));
    }

    [TestMethod]
    public void ArchiveFieldsPresent_FiresOnEitherHalfMissing()
    {
        var noReason = Entry("a") with { ArchivedAt = Now.AddDays(-1) };
        var noTimestamp = Entry("b") with { ArchiveReason = "ephemeral" };

        var violations = Check([noReason, noTimestamp], Snapshot());

        var violation = violations.Single(v => v.Name == MemoryAuditInvariants.ArchiveFieldsPresent);
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, violation.Ids.ToArray());
    }

    [TestMethod]
    public void LiveNotMergeSource_FiresWhenAMergedAwayEntryIsStillInRecall()
    {
        var stillLive = Entry("src");
        var merged = Entry("merged") with { Metadata = MergedFrom("src") };

        var violations = Check([stillLive, merged], Snapshot());

        var violation = violations.Single(v => v.Name == MemoryAuditInvariants.LiveNotMergeSource);
        CollectionAssert.AreEquivalent(new[] { "src" }, violation.Ids.ToArray());
        Assert.AreEqual(MemoryAuditStatuses.Warning, MemoryAuditInvariants.ComputeStatus(violations));
    }

    [TestMethod]
    public void NoHardDeleteOutsidePurge_FiresOnASingleUnexplainedDisappearance()
    {
        var violations = Check([], Snapshot() with { HardDeletedOutsidePurge = 1 });

        Assert.IsTrue(violations.Any(v => v.Name == MemoryAuditInvariants.NoHardDeleteOutsidePurge));
        Assert.AreEqual(MemoryAuditStatuses.Alert, MemoryAuditInvariants.ComputeStatus(violations));
    }

    [TestMethod]
    public void LossPercentThreshold_FiresOnADropPastTheLimit()
    {
        var violations = Check([], Snapshot() with { Live = 80 }, previousLive: 100);

        Assert.IsTrue(violations.Any(v => v.Name == MemoryAuditInvariants.LossPercentThreshold));
    }

    [TestMethod]
    public void LossPercentThreshold_DoesNotFireOnGrowth()
    {
        var violations = Check([], Snapshot() with { Live = 120 }, previousLive: 100);

        Assert.IsFalse(violations.Any(v => v.Name == MemoryAuditInvariants.LossPercentThreshold));
    }

    [TestMethod]
    public void NetGrowthThreshold_FiresWhenSavesOutpaceConsolidation()
    {
        var violations = Check([], Snapshot() with { NetGrowthPerDay = 12.0 });

        Assert.IsTrue(violations.Any(v => v.Name == MemoryAuditInvariants.NetGrowthThreshold));
        Assert.AreEqual(MemoryAuditStatuses.Warning, MemoryAuditInvariants.ComputeStatus(violations));
    }

    [TestMethod]
    public void ChainDepthThreshold_FiresPastTheConfiguredDepth()
    {
        var violations = Check([], Snapshot() with { MaxChainDepth = 3 });

        Assert.IsTrue(violations.Any(v => v.Name == MemoryAuditInvariants.ChainDepthThreshold));
    }

    [TestMethod]
    public void RejectedMergesThreshold_IsMeasuredPerWeekNotPerRun()
    {
        // Four rejections in one day is 28/week, well past the default of 5.
        var violations = Check([], Snapshot() with { RejectedMergeSourcesSinceLast = 4 }, elapsedDays: 1);
        Assert.IsTrue(violations.Any(v => v.Name == MemoryAuditInvariants.RejectedMergesThreshold));

        // The same four spread over a month is not.
        var calm = Check([], Snapshot() with { RejectedMergeSourcesSinceLast = 4 }, elapsedDays: 30);
        Assert.IsFalse(calm.Any(v => v.Name == MemoryAuditInvariants.RejectedMergesThreshold));
    }

    [TestMethod]
    public void MalformedFilesAreReported()
    {
        var violations = Check([], Snapshot() with { MalformedFiles = 2 });

        Assert.IsTrue(violations.Any(v => v.Name == MemoryAuditInvariants.NoMalformedFiles));
    }

    [TestMethod]
    public void ViolationIdsAreCappedSoAMassFailureDoesNotBloatTheTrendFile()
    {
        var entries = Enumerable.Range(0, 100)
            .Select(i => Archived($"e{i}", $"{DreamService.MergedIntoReasonPrefix}vanished"))
            .ToList();

        var violation = Check(entries, Snapshot())
            .Single(v => v.Name == MemoryAuditInvariants.MergeChainUnbroken);

        Assert.AreEqual(MemoryAuditAnalyzer.MaxIdsPerViolation, violation.Ids.Count);
        StringAssert.Contains(violation.Message, "100", "The message still reports the true count.");
    }

    [TestMethod]
    public void AnAlertOutranksAWarning()
    {
        var violations = Check([], Snapshot() with
        {
            NetGrowthPerDay = 12,
            HardDeletedOutsidePurge = 1
        });

        Assert.AreEqual(MemoryAuditStatuses.Alert, MemoryAuditInvariants.ComputeStatus(violations));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<MemoryAuditInvariantViolation> Check(
        IReadOnlyList<MemoryEntry> entries,
        MemoryAuditSnapshot snapshot,
        double elapsedDays = 1,
        int? previousLive = null) =>
        MemoryAuditInvariants.Check(
            entries, snapshot, new DreamOptions(), new MemoryAuditOptions(), Now, elapsedDays, previousLive);

    private static MemoryAuditSnapshot Snapshot() => new()
    {
        SnapshotId = "snap1",
        TakenAt = Now,
        PreviousTakenAt = Now.AddDays(-1)
    };

    private static MemoryEntry Entry(string id) =>
        new(id, $"content for {id}", null, [], Now.AddDays(-30));

    private static MemoryEntry Archived(string id, string reason) =>
        Entry(id) with { ArchivedAt = Now.AddDays(-2), ArchiveReason = reason };

    private static Dictionary<string, string> MergedFrom(params string[] ids) => new()
    {
        [DreamService.MergedFromKey] = string.Join(",", ids),
        [DreamService.MergedAtKey] = Now.AddDays(-1).ToString("O")
    };
}
