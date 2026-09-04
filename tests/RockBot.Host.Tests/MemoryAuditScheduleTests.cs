using Cronos;

namespace RockBot.Host.Tests;

/// <summary>
/// One timer serves both the daily measurement and the weekly eval, so it has to be armed at
/// whichever comes first.
/// </summary>
[TestClass]
public class MemoryAuditScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [TestMethod]
    public void PicksTheAuditWhenItComesFirst()
    {
        // Friday 06:00: the next daily audit is Saturday 04:00, the next eval Sunday 05:00.
        var now = new DateTimeOffset(2026, 9, 4, 6, 0, 0, TimeSpan.Zero);

        var next = MemoryAuditService.ComputeNextDue(
            now, Utc, CronExpression.Parse("0 4 * * *"), CronExpression.Parse("0 5 * * 0"));

        Assert.AreEqual(new DateTimeOffset(2026, 9, 5, 4, 0, 0, TimeSpan.Zero), next);
    }

    [TestMethod]
    public void PicksTheEvalWhenItComesFirst()
    {
        // Sunday 04:30: that day's audit has passed, the eval is half an hour away.
        var now = new DateTimeOffset(2026, 9, 6, 4, 30, 0, TimeSpan.Zero);

        var next = MemoryAuditService.ComputeNextDue(
            now, Utc, CronExpression.Parse("0 4 * * *"), CronExpression.Parse("0 5 * * 0"));

        Assert.AreEqual(new DateTimeOffset(2026, 9, 6, 5, 0, 0, TimeSpan.Zero), next);
    }

    [TestMethod]
    public void FallsBackToTheAuditWhenTheEvalIsDisabled()
    {
        var now = new DateTimeOffset(2026, 9, 6, 4, 30, 0, TimeSpan.Zero);

        var next = MemoryAuditService.ComputeNextDue(
            now, Utc, CronExpression.Parse("0 4 * * *"), eval: null);

        Assert.AreEqual(new DateTimeOffset(2026, 9, 7, 4, 0, 0, TimeSpan.Zero), next);
    }

    [TestMethod]
    public void CoincidingSchedulesYieldTheSharedOccurrenceOnce()
    {
        var now = new DateTimeOffset(2026, 9, 4, 6, 0, 0, TimeSpan.Zero);

        var next = MemoryAuditService.ComputeNextDue(
            now, Utc, CronExpression.Parse("0 4 * * *"), CronExpression.Parse("0 4 * * *"));

        Assert.AreEqual(new DateTimeOffset(2026, 9, 5, 4, 0, 0, TimeSpan.Zero), next);
    }
}
