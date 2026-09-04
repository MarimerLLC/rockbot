namespace RockBot.Host.Tests;

/// <summary>
/// The report is the deliverable a person actually reads, so its shape is worth pinning:
/// status first, what changed, what needs attention, then the trend.
/// </summary>
[TestClass]
public class MemoryAuditReportWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AHealthyReportSaysSoAndListsNothingToAttendTo()
    {
        var report = MemoryAuditReportWriter.Render(Snapshot(), [Snapshot()]);

        StringAssert.Contains(report, "**Healthy**");
        StringAssert.Contains(report, "820 live entries");
        StringAssert.Contains(report, "## Needs attention");
        StringAssert.Contains(report, "Nothing.");
    }

    [TestMethod]
    public void AnAlertNamesTheInvariantAndItsIds()
    {
        var snapshot = Snapshot() with
        {
            Status = MemoryAuditStatuses.Alert,
            HardDeletedOutsidePurge = 74,
            Invariants =
            [
                new MemoryAuditInvariantViolation(
                    MemoryAuditInvariants.NoHardDeleteOutsidePurge,
                    "74 entry(s) disappeared from disk that the retention purge cannot account for.",
                    ["lost1", "lost2"])
            ]
        };

        var report = MemoryAuditReportWriter.Render(snapshot, [snapshot]);

        StringAssert.Contains(report, "**ALERT**");
        StringAssert.Contains(report, "no-hard-delete-outside-purge");
        StringAssert.Contains(report, "74 entry(s) disappeared");
        StringAssert.Contains(report, "`lost1`");
    }

    [TestMethod]
    public void AnUnmeasurableRateReadsAsSuchRatherThanAsZero()
    {
        var snapshot = Snapshot() with { NetGrowthPerDay = null };

        var report = MemoryAuditReportWriter.Render(snapshot, [snapshot, snapshot]);

        StringAssert.Contains(report, "not measurable");
        StringAssert.Contains(report, "| — |", "The trend table shows a dash, not a zero.");
    }

    [TestMethod]
    public void AFirstRunSaysThereIsNothingToCompareAgainst()
    {
        var report = MemoryAuditReportWriter.Render(
            Snapshot() with { PreviousTakenAt = null }, [Snapshot()]);

        StringAssert.Contains(report, "First run");
    }

    [TestMethod]
    public void TheTrendTableCarriesOneRowPerRun()
    {
        var trend = Enumerable.Range(0, 5)
            .Select(i => Snapshot() with { TakenAt = Now.AddDays(-4 + i), Live = 800 + i })
            .ToList();

        var report = MemoryAuditReportWriter.Render(trend[^1], trend);

        StringAssert.Contains(report, "## Trend (last 5 runs)");
        foreach (var row in trend)
            StringAssert.Contains(report, $"| {row.TakenAt:yyyy-MM-dd} | {row.Live} |");
    }

    [TestMethod]
    public void ASingleRunOmitsTheTrendTableRatherThanShowingOneRow()
    {
        var report = MemoryAuditReportWriter.Render(Snapshot(), [Snapshot()]);

        Assert.IsFalse(report.Contains("## Trend"));
    }

    [TestMethod]
    public void TheEvalParagraphAppearsWhenAnEvalExistsAndNamesTheDisagreements()
    {
        var eval = new MemoryAuditEvalResult(
            new MemoryAuditEvalSummary(
                Now.AddDays(-1), 10, 8, 0.8,
                new Dictionary<string, double> { ["merge"] = 0.75 }),
            [
                new MemoryAuditEvalVerdict("merge", ["m1", "s1"], false, "Dropped the account number."),
                new MemoryAuditEvalVerdict("merge", ["m2"], true, "Kept every specific.")
            ],
            "FINGERPRINT");

        var report = MemoryAuditReportWriter.Render(Snapshot(), [Snapshot()], eval);

        StringAssert.Contains(report, "## Sample eval");
        StringAssert.Contains(report, "merge: 75%");
        StringAssert.Contains(report, "Dropped the account number.");
        Assert.IsFalse(report.Contains("Kept every specific."),
            "Only disagreements are worth the reader's attention.");
    }

    [TestMethod]
    public void NoEvalSectionAppearsBeforeTheFirstEvalHasRun()
    {
        var report = MemoryAuditReportWriter.Render(Snapshot(), [Snapshot()]);

        Assert.IsFalse(report.Contains("## Sample eval"));
    }

    private static MemoryAuditSnapshot Snapshot() => new()
    {
        SnapshotId = "snap1",
        TakenAt = Now,
        PreviousTakenAt = Now.AddDays(-1),
        Live = 820,
        Archived = 340,
        CreatedSinceLast = 12,
        ArchivedSinceLast = 8,
        NetGrowthPerDay = 4,
        MaxChainDepth = 1,
        NearDupPairs = 6,
        NearDupEntries = 11,
        Purge = new MemoryAuditPurgeOutlook(7, 3, 1)
    };
}
