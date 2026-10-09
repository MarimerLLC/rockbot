using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public class SessionTierHistoryTests
{
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void NoHistory_ReturnsNull()
    {
        var history = new SessionTierHistory();

        Assert.IsNull(history.GetRecentMax("s1"));
    }

    [TestMethod]
    public void RecentMax_IsHighestOfRecentTurns_PerSession()
    {
        var history = new SessionTierHistory();
        history.Record("s1", "t1", ModelTier.Low);
        history.Record("s1", "t2", ModelTier.High);
        history.Record("s1", "t3", ModelTier.Balanced);
        history.Record("s2", "t1", ModelTier.Low);

        Assert.AreEqual(ModelTier.High, history.GetRecentMax("s1"));
        Assert.AreEqual(ModelTier.Low, history.GetRecentMax("s2"));
    }

    [TestMethod]
    public void InheritedTier_DecaysAfterMaxTurnsPerSession()
    {
        var history = new SessionTierHistory();
        history.Record("s1", "t0", ModelTier.High);
        for (var i = 1; i < SessionTierHistory.MaxTurnsPerSession; i++)
            history.Record("s1", $"t{i}", ModelTier.Balanced);

        Assert.AreEqual(ModelTier.High, history.GetRecentMax("s1"), "Still within the window.");

        history.Record("s1", "tN", ModelTier.Balanced);

        Assert.AreEqual(ModelTier.Balanced, history.GetRecentMax("s1"),
            "The High turn falls out of the window after MaxTurnsPerSession newer turns.");
    }

    [TestMethod]
    public void Entries_ExpireAfterThreadRecency()
    {
        var time = new ManualTime(T0);
        var history = new SessionTierHistory(time);
        history.Record("s1", "t1", ModelTier.High);

        time.Now = T0 + ShortMessageHeuristics.ThreadEstablishedRecency - TimeSpan.FromMinutes(1);
        Assert.AreEqual(ModelTier.High, history.GetRecentMax("s1"));

        time.Now = T0 + ShortMessageHeuristics.ThreadEstablishedRecency + TimeSpan.FromMinutes(1);
        Assert.IsNull(history.GetRecentMax("s1"), "A stale thread passes nothing on.");
    }

    [TestMethod]
    public void Raise_LiftsTheTurnsOwnEntry_WithoutAddingOne()
    {
        var history = new SessionTierHistory();
        history.Record("s1", "old", ModelTier.Balanced);
        history.Record("s1", "t1", ModelTier.Low);
        history.Raise("s1", "t1", ModelTier.Balanced);
        history.Raise("s1", "t1", ModelTier.Low); // never lowers
        history.Record("s1", "t2", ModelTier.Low);
        history.Record("s1", "t3", ModelTier.Low);

        // Window is t1 (raised to Balanced), t2, t3 — "old" fell out, so Balanced comes from t1.
        Assert.AreEqual(ModelTier.Balanced, history.GetRecentMax("s1"));

        history.Record("s1", "t4", ModelTier.Low);
        Assert.AreEqual(ModelTier.Low, history.GetRecentMax("s1"));
    }

    [TestMethod]
    public void Clear_ForgetsTheSession()
    {
        var history = new SessionTierHistory();
        history.Record("s1", "t1", ModelTier.High);

        history.Clear("s1");

        Assert.IsNull(history.GetRecentMax("s1"));
    }

    [TestMethod]
    public void SessionCount_IsBounded_EvictingLeastRecentlyTouched()
    {
        var time = new ManualTime(T0);
        var history = new SessionTierHistory(time);
        for (var i = 0; i < SessionTierHistory.MaxSessions; i++)
        {
            time.Now = T0 + TimeSpan.FromMilliseconds(i);
            history.Record($"s{i}", "t", ModelTier.Balanced);
        }

        time.Now = T0 + TimeSpan.FromSeconds(10);
        history.Record("newcomer", "t", ModelTier.High);

        Assert.IsNull(history.GetRecentMax("s0"), "The least recently touched session is evicted.");
        Assert.AreEqual(ModelTier.Balanced, history.GetRecentMax("s1"));
        Assert.AreEqual(ModelTier.High, history.GetRecentMax("newcomer"));
    }

    [TestMethod]
    public async Task ConcurrentRecords_AreSafe()
    {
        var history = new SessionTierHistory();
        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            history.Record($"s{i % 4}", $"t{i}", (ModelTier)(i % 3));
            _ = history.GetRecentMax($"s{i % 4}");
        })));

        for (var s = 0; s < 4; s++)
            Assert.IsNotNull(history.GetRecentMax($"s{s}"));
    }
}
