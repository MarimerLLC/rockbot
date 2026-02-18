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
