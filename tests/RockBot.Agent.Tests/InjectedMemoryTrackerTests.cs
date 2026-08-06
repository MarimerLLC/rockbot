using RockBot.Memory;

namespace RockBot.Memory.Tests;

[TestClass]
public class InjectedMemoryTrackerTests
{
    [TestMethod]
    public void TryMarkAsInjected_FirstTime_ReturnsTrue()
    {
        var tracker = new InjectedMemoryTracker();
        Assert.IsTrue(tracker.TryMarkAsInjected("session-1", "mem-abc"));
    }

    [TestMethod]
    public void TryMarkAsInjected_SecondTime_ReturnsFalse()
    {
        var tracker = new InjectedMemoryTracker();
        tracker.TryMarkAsInjected("session-1", "mem-abc");
        Assert.IsFalse(tracker.TryMarkAsInjected("session-1", "mem-abc"));
    }

    [TestMethod]
    public void TryMarkAsInjected_SameIdDifferentSessions_BothReturnTrue()
    {
        var tracker = new InjectedMemoryTracker();
        Assert.IsTrue(tracker.TryMarkAsInjected("session-1", "mem-abc"));
        Assert.IsTrue(tracker.TryMarkAsInjected("session-2", "mem-abc"));
    }

    [TestMethod]
    public void TryMarkAsInjected_DifferentIdsInSameSession_AllReturnTrue()
    {
        var tracker = new InjectedMemoryTracker();
        Assert.IsTrue(tracker.TryMarkAsInjected("session-1", "mem-aaa"));
        Assert.IsTrue(tracker.TryMarkAsInjected("session-1", "mem-bbb"));
        Assert.IsTrue(tracker.TryMarkAsInjected("session-1", "mem-ccc"));
    }

    [TestMethod]
    public void Clear_AllowsReInjection()
    {
        var tracker = new InjectedMemoryTracker();
        tracker.TryMarkAsInjected("session-1", "mem-abc");
        tracker.Clear("session-1");
        Assert.IsTrue(tracker.TryMarkAsInjected("session-1", "mem-abc"),
            "After Clear, the same ID should be injectable again");
    }

    [TestMethod]
    public void TryMarkAsInjected_WithinVisibleWindow_StaysSuppressed()
    {
        var tracker = new InjectedMemoryTracker();
        tracker.TryMarkAsInjected("session-1", "mem-abc", currentTurn: 5, visibleHistoryTurns: 20);

        // Turn 24 is still within 20 turns of the injection at turn 5, so the entry remains
        // visible in context and must not be injected a second time.
        Assert.IsFalse(tracker.TryMarkAsInjected(
            "session-1", "mem-abc", currentTurn: 24, visibleHistoryTurns: 20));
    }

    [TestMethod]
    public void TryMarkAsInjected_AfterScrollingOutOfWindow_IsReInjected()
    {
        var tracker = new InjectedMemoryTracker();
        tracker.TryMarkAsInjected("session-1", "mem-abc", currentTurn: 5, visibleHistoryTurns: 20);

        // At turn 25 the injecting turn has fallen out of the visible window, so the entry is
        // no longer in context and has to be re-injected for the model to see it.
        Assert.IsTrue(tracker.TryMarkAsInjected(
            "session-1", "mem-abc", currentTurn: 25, visibleHistoryTurns: 20));
    }

    [TestMethod]
    public void TryMarkAsInjected_ReInjectionResetsTheWindow()
    {
        var tracker = new InjectedMemoryTracker();
        tracker.TryMarkAsInjected("session-1", "mem-abc", currentTurn: 0, visibleHistoryTurns: 10);
        // Expires and is re-injected at turn 10, which restarts the window from there.
        tracker.TryMarkAsInjected("session-1", "mem-abc", currentTurn: 10, visibleHistoryTurns: 10);

        Assert.IsFalse(tracker.TryMarkAsInjected(
            "session-1", "mem-abc", currentTurn: 15, visibleHistoryTurns: 10),
            "Turn 15 is within 10 turns of the re-injection at turn 10");
        Assert.IsTrue(tracker.TryMarkAsInjected(
            "session-1", "mem-abc", currentTurn: 20, visibleHistoryTurns: 10),
            "Turn 20 is a full window past the re-injection at turn 10");
    }

    [TestMethod]
    public void TryMarkAsInjected_WithoutVisibleWindow_NeverExpires()
    {
        var tracker = new InjectedMemoryTracker();
        tracker.TryMarkAsInjected("session-1", "mem-abc", currentTurn: 0);

        // No window supplied → expiry disabled, preserving inject-at-most-once behaviour.
        Assert.IsFalse(tracker.TryMarkAsInjected("session-1", "mem-abc", currentTurn: 10_000));
    }

    [TestMethod]
    public void TryMarkAsInjected_ExpiryIsPerMemoryId()
    {
        var tracker = new InjectedMemoryTracker();
        tracker.TryMarkAsInjected("session-1", "mem-old", currentTurn: 0, visibleHistoryTurns: 20);
        tracker.TryMarkAsInjected("session-1", "mem-new", currentTurn: 21, visibleHistoryTurns: 20);

        Assert.IsTrue(tracker.TryMarkAsInjected(
            "session-1", "mem-old", currentTurn: 25, visibleHistoryTurns: 20),
            "mem-old was injected at turn 0 and has scrolled out");
        Assert.IsFalse(tracker.TryMarkAsInjected(
            "session-1", "mem-new", currentTurn: 25, visibleHistoryTurns: 20),
            "mem-new was injected at turn 21 and is still visible");
    }

    [TestMethod]
    public void Clear_NonexistentSession_NoOp()
    {
        var tracker = new InjectedMemoryTracker();
        // Should not throw
        tracker.Clear("ghost-session");
    }

    [TestMethod]
    public void Clear_OnlyAffectsTargetSession()
    {
        var tracker = new InjectedMemoryTracker();
        tracker.TryMarkAsInjected("session-1", "mem-abc");
        tracker.TryMarkAsInjected("session-2", "mem-abc");

        tracker.Clear("session-1");

        // session-1 was cleared → re-injection allowed
        Assert.IsTrue(tracker.TryMarkAsInjected("session-1", "mem-abc"));
        // session-2 was not cleared → still blocked
        Assert.IsFalse(tracker.TryMarkAsInjected("session-2", "mem-abc"));
    }
}
