namespace RockBot.Host.Tests;

/// <summary>
/// Covers the backoff that keeps a dream cycle alive when other agent work holds the slot.
/// </summary>
/// <remarks>
/// The work serializer is acquired non-blockingly, so a cycle firing while a patrol, scheduled
/// task, or user turn is running used to be abandoned until the next cron occurrence — twelve
/// hours away by default. A patrol on its own schedule can overlap the same cron slot every day,
/// which turns "the dream was skipped once" into "the dream never runs".
/// </remarks>
[TestClass]
public class DreamContentionBackoffTests
{
    private static readonly TimeSpan Initial = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Max = TimeSpan.FromHours(1);

    [TestMethod]
    public void Delay_GrowsGeometricallyThenCaps()
    {
        TimeSpan D(int attempt) =>
            DreamService.ComputeContentionRetryDelay(attempt, Initial, 2.0, Max);

        Assert.AreEqual(TimeSpan.FromMinutes(5), D(0));
        Assert.AreEqual(TimeSpan.FromMinutes(10), D(1));
        Assert.AreEqual(TimeSpan.FromMinutes(20), D(2));
        Assert.AreEqual(TimeSpan.FromMinutes(40), D(3));
        Assert.AreEqual(Max, D(4), "80 minutes exceeds the ceiling, so the ceiling applies.");
        Assert.AreEqual(Max, D(5));
    }

    [TestMethod]
    public void Delay_LargeAttemptCountCannotOverflow()
    {
        // Math.Pow(2, 10_000) is +Infinity; without clamping this throws rather than capping.
        Assert.AreEqual(Max, DreamService.ComputeContentionRetryDelay(10_000, Initial, 2.0, Max));
    }

    [TestMethod]
    public void Delay_MultiplierOfOneOrLess_StaysAtTheInitialDelay()
    {
        Assert.AreEqual(Initial, DreamService.ComputeContentionRetryDelay(5, Initial, 1.0, Max));
        Assert.AreEqual(Initial, DreamService.ComputeContentionRetryDelay(5, Initial, 0.5, Max));
    }

    [TestMethod]
    public void Delay_MaxBelowInitial_NeverReturnsLessThanInitial()
    {
        // A misconfiguration must not collapse the backoff into a tight retry loop against a
        // busy agent.
        var delay = DreamService.ComputeContentionRetryDelay(
            3, Initial, 2.0, TimeSpan.FromSeconds(1));

        Assert.AreEqual(Initial, delay);
    }

    [TestMethod]
    public void Delay_ZeroInitial_IsZero()
    {
        Assert.AreEqual(
            TimeSpan.Zero,
            DreamService.ComputeContentionRetryDelay(2, TimeSpan.Zero, 2.0, Max));
    }

    [TestMethod]
    public void DefaultSchedule_LandsTheCycleWithinAFewHours()
    {
        // The point of the defaults: a collision should cost minutes, not the twelve hours a
        // dropped cycle used to cost.
        var opts = new DreamOptions();
        var total = TimeSpan.Zero;
        for (var i = 0; i < opts.DreamContentionMaxRetries; i++)
        {
            total += DreamService.ComputeContentionRetryDelay(
                i,
                opts.DreamContentionRetryInitialDelay,
                opts.DreamContentionRetryMultiplier,
                opts.DreamContentionRetryMaxDelay);
        }

        Assert.AreEqual(TimeSpan.FromMinutes(5 + 10 + 20 + 40 + 60 + 60), total);
        Assert.IsTrue(total < TimeSpan.FromHours(4));
    }

    [TestMethod]
    public void Defaults_AreOnAndReasonable()
    {
        var opts = new DreamOptions();

        Assert.IsTrue(opts.DeferDreamOnContention);
        Assert.AreEqual(TimeSpan.FromMinutes(5), opts.DreamContentionRetryInitialDelay);
        Assert.AreEqual(2.0, opts.DreamContentionRetryMultiplier);
        Assert.AreEqual(TimeSpan.FromHours(1), opts.DreamContentionRetryMaxDelay);
        Assert.AreEqual(6, opts.DreamContentionMaxRetries);
    }
}
