using RockBot.Wisp;

namespace RockBot.Wisp.Tests;

[TestClass]
public class WispDispatchCircuitBreakerTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);

    private static (WispDispatchCircuitBreaker Breaker, TestClock Clock) NewBreaker(
        int max = 3, int windowMinutes = 5, bool enabled = true)
    {
        var options = new WispOptions
        {
            DispatchCircuitBreakerEnabled = enabled,
            DispatchCircuitBreakerMaxPerWindow = max,
            DispatchCircuitBreakerWindow = TimeSpan.FromMinutes(windowMinutes),
        };
        var clock = new TestClock(Start);
        return (new WispDispatchCircuitBreaker(options, clock), clock);
    }

    [TestMethod]
    public void Admit_UpToLimit_AllAllowed_ThenTrips()
    {
        var (breaker, _) = NewBreaker(max: 3);

        for (var i = 1; i <= 3; i++)
        {
            var d = breaker.Admit("hash-a");
            Assert.IsTrue(d.Allowed, $"Dispatch {i} (≤ limit) must be allowed");
            Assert.AreEqual(i, d.Count);
        }

        var tripped = breaker.Admit("hash-a");
        Assert.IsFalse(tripped.Allowed, "The dispatch that crosses the limit must be refused");
        Assert.AreEqual(4, tripped.Count);
    }

    [TestMethod]
    public void Admit_StaysTrippedForRestOfWindow()
    {
        var (breaker, clock) = NewBreaker(max: 2, windowMinutes: 5);

        breaker.Admit("h");
        breaker.Admit("h");
        Assert.IsFalse(breaker.Admit("h").Allowed);

        // Still inside the window a few minutes later → still refused.
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.IsFalse(breaker.Admit("h").Allowed, "Must remain tripped until the window rolls over");
    }

    [TestMethod]
    public void Admit_AfterWindowExpiry_ResetsAndAllows()
    {
        var (breaker, clock) = NewBreaker(max: 2, windowMinutes: 5);

        breaker.Admit("h");
        breaker.Admit("h");
        Assert.IsFalse(breaker.Admit("h").Allowed);

        clock.Advance(TimeSpan.FromMinutes(5)); // window fully elapsed
        var afterReset = breaker.Admit("h");
        Assert.IsTrue(afterReset.Allowed, "A new window must allow dispatches again");
        Assert.AreEqual(1, afterReset.Count, "Count must reset at the window boundary");
    }

    [TestMethod]
    public void Admit_DifferentDefinitions_AreCountedIndependently()
    {
        var (breaker, _) = NewBreaker(max: 2);

        breaker.Admit("a");
        breaker.Admit("a");
        Assert.IsFalse(breaker.Admit("a").Allowed, "'a' should be tripped");

        // A different definition hash has its own budget.
        Assert.IsTrue(breaker.Admit("b").Allowed, "'b' must be unaffected by 'a' tripping");
        Assert.IsTrue(breaker.Admit("b").Allowed);
        Assert.IsFalse(breaker.Admit("b").Allowed, "'b' trips on its own threshold");
    }

    [TestMethod]
    public void Admit_WhenDisabled_AlwaysAllows()
    {
        var (breaker, _) = NewBreaker(max: 1, enabled: false);

        for (var i = 0; i < 100; i++)
            Assert.IsTrue(breaker.Admit("h").Allowed, "Disabled breaker must never refuse");
    }

    [TestMethod]
    public void Admit_WithNonPositiveLimit_AlwaysAllows()
    {
        var (breaker, _) = NewBreaker(max: 0);

        for (var i = 0; i < 100; i++)
            Assert.IsTrue(breaker.Admit("h").Allowed, "A non-positive limit disables the breaker");
    }

    [TestMethod]
    public void Admit_EmptyHash_AlwaysAllows()
    {
        var (breaker, _) = NewBreaker(max: 1);

        Assert.IsTrue(breaker.Admit("").Allowed);
        Assert.IsTrue(breaker.Admit("").Allowed, "An empty/unknown definition hash is never gated");
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
