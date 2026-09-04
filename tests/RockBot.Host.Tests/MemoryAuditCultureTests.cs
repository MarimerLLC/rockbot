using System.Globalization;

namespace RockBot.Host.Tests;

/// <summary>
/// The audit's output must be byte-identical whatever locale the host runs under.
/// </summary>
/// <remarks>
/// <para>
/// This exists because CI caught what a developer machine could not. A percentage written with
/// the <c>P0</c> specifier renders as <c>75%</c> under en-US and as <c>75 %</c> — with a space —
/// under the invariant culture that a container with no locale actually runs with. The same
/// split applies to decimal separators (<c>4.0</c> vs <c>4,0</c>) and to non-Gregorian calendars.
/// </para>
/// <para>
/// It matters beyond cosmetics: these strings are stored in the JSON trend rows and read back by
/// the introspection sidecar, so a locale change would silently alter the on-disk record.
/// </para>
/// </remarks>
[TestClass]
public class MemoryAuditCultureTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);

    /// <summary>Cultures that break each of the assumptions above.</summary>
    private static IEnumerable<object[]> HostileCultures =>
    [
        [""],           // invariant — what a container with no locale gets
        ["de-DE"],      // decimal comma, different percent spacing
        ["fr-FR"],      // narrow no-break space as group separator
        ["th-TH"]       // Buddhist calendar: year 2569, not 2026
    ];

    [TestMethod]
    [DynamicData(nameof(HostileCultures))]
    public void TheReportRendersIdenticallyUnderAnyCulture(string cultureName)
    {
        var reference = RenderUnder(Culture("en-US"));
        var actual = RenderUnder(Culture(cultureName));

        Assert.AreEqual(reference, actual,
            $"The report changed shape under '{cultureName}'.");
    }

    [TestMethod]
    public void PercentagesCarryNoSpaceBeforeTheSign()
    {
        // The exact failure CI reported: "P0" under the invariant culture yields "75 %".
        var report = RenderUnder(CultureInfo.InvariantCulture);

        StringAssert.Contains(report, "75% sound");
        Assert.IsFalse(report.Contains("75 %"), "A space crept back in before the percent sign.");
    }

    [TestMethod]
    public void DatesStayGregorianUnderANonGregorianCalendar()
    {
        var report = RenderUnder(Culture("th-TH"));

        StringAssert.Contains(report, "2026-09-04");
        Assert.IsFalse(report.Contains("2569"), "The Buddhist calendar leaked into the report.");
    }

    [TestMethod]
    public void InvariantMessagesUseADotDecimalSeparator()
    {
        var snapshot = Snapshot() with { Live = 80, NetGrowthPerDay = 12.5 };

        var violations = RunUnder(Culture("de-DE"), () =>
            MemoryAuditInvariants.Check(
                [], snapshot, new DreamOptions(), new MemoryAuditOptions(),
                Now, elapsedDays: 1, previousLive: 100));

        var growth = violations.Single(v => v.Name == MemoryAuditInvariants.NetGrowthThreshold);
        StringAssert.Contains(growth.Message, "12.5");
        Assert.IsFalse(growth.Message.Contains("12,5"), "A decimal comma reached a stored message.");

        var loss = violations.Single(v => v.Name == MemoryAuditInvariants.LossPercentThreshold);
        StringAssert.Contains(loss.Message, "20.0%");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The named culture, or <c>Assert.Inconclusive</c> when the runtime cannot supply it.
    /// A host in globalization-invariant mode has only one culture, so there is nothing to
    /// compare — matching how the RabbitMQ integration tests report an absent dependency.
    /// </summary>
    private static CultureInfo Culture(string name)
    {
        if (name.Length == 0) return CultureInfo.InvariantCulture;

        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            Assert.Inconclusive(
                $"'{name}' is unavailable — the runtime is in globalization-invariant mode, " +
                "so there is no second culture to compare against.");
            throw;
        }
    }

    private static string RenderUnder(CultureInfo culture) =>
        RunUnder(culture, () =>
        {
            var snapshot = Snapshot();
            var eval = new MemoryAuditEvalResult(
                new MemoryAuditEvalSummary(
                    Now.AddDays(-1), 8, 6, 0.75,
                    new Dictionary<string, double> { ["merge"] = 0.75 }),
                [new MemoryAuditEvalVerdict("merge", ["m1"], false, "Dropped a number.")],
                "FP");

            return MemoryAuditReportWriter.Render(
                snapshot, [snapshot with { TakenAt = Now.AddDays(-1) }, snapshot], eval);
        });

    private static T RunUnder<T>(CultureInfo culture, Func<T> body)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static MemoryAuditSnapshot Snapshot() => new()
    {
        SnapshotId = "snap1",
        TakenAt = Now,
        PreviousTakenAt = Now.AddDays(-1),
        Live = 1820,
        Archived = 1340,
        CreatedSinceLast = 1234,
        ArchivedSinceLast = 8,
        NetGrowthPerDay = 4.5,
        MaxChainDepth = 1,
        NearDupPairs = 6,
        NearDupEntries = 11,
        Purge = new MemoryAuditPurgeOutlook(7, 3, 1),
        TopCategoriesByGrowth = [new MemoryAuditCategoryGrowth("chores", 0, 2, -2)]
    };
}
