namespace RockBot.Host.Tests;

[TestClass]
public class SessionBackgroundTaskTrackerTests
{
    [TestMethod]
    public void HasActiveUserLoop_ReturnsFalse_WhenNoSessionStarted()
    {
        using var tracker = new SessionBackgroundTaskTracker();

        Assert.IsFalse(tracker.HasActiveUserLoop("session-1"));
    }

    [TestMethod]
    public void HasActiveUserLoop_ReturnsTrue_AfterBeginSession()
    {
        using var tracker = new SessionBackgroundTaskTracker();

        tracker.BeginSession("session-1", CancellationToken.None);

        Assert.IsTrue(tracker.HasActiveUserLoop("session-1"));
    }

    [TestMethod]
    public void HasActiveUserLoop_ReturnsFalse_AfterEndSession()
    {
        using var tracker = new SessionBackgroundTaskTracker();
        var handle = tracker.BeginSession("session-1", CancellationToken.None);

        tracker.EndSession("session-1", handle.Generation);

        Assert.IsFalse(tracker.HasActiveUserLoop("session-1"));
    }

    [TestMethod]
    public void HasActiveUserLoop_ReturnsTrue_WhenNewSessionStartedAfterEnd()
    {
        using var tracker = new SessionBackgroundTaskTracker();
        var handle1 = tracker.BeginSession("session-1", CancellationToken.None);
        tracker.EndSession("session-1", handle1.Generation);

        tracker.BeginSession("session-1", CancellationToken.None);

        Assert.IsTrue(tracker.HasActiveUserLoop("session-1"));
    }

    [TestMethod]
    public void EndSession_StaleGeneration_DoesNotDeactivateNewerLoop()
    {
        using var tracker = new SessionBackgroundTaskTracker();
        var handle1 = tracker.BeginSession("session-1", CancellationToken.None);

        // New message arrives before old loop calls EndSession
        var handle2 = tracker.BeginSession("session-1", CancellationToken.None);

        // Old loop finishes and calls EndSession with stale generation
        tracker.EndSession("session-1", handle1.Generation);

        // New loop should still be active
        Assert.IsTrue(tracker.HasActiveUserLoop("session-1"));
    }

    [TestMethod]
    public void BeginSession_CancelsPreviousToken()
    {
        using var tracker = new SessionBackgroundTaskTracker();
        var handle1 = tracker.BeginSession("session-1", CancellationToken.None);

        tracker.BeginSession("session-1", CancellationToken.None);

        Assert.IsTrue(handle1.Token.IsCancellationRequested);
    }

    [TestMethod]
    public void BeginSession_ReturnsUncancelledToken()
    {
        using var tracker = new SessionBackgroundTaskTracker();

        var handle = tracker.BeginSession("session-1", CancellationToken.None);

        Assert.IsFalse(handle.Token.IsCancellationRequested);
    }

    [TestMethod]
    public void BeginSession_LinkedToHostToken_CancelsWhenHostCancels()
    {
        using var tracker = new SessionBackgroundTaskTracker();
        using var hostCts = new CancellationTokenSource();

        var handle = tracker.BeginSession("session-1", hostCts.Token);
        hostCts.Cancel();

        Assert.IsTrue(handle.Token.IsCancellationRequested);
    }

    [TestMethod]
    public void GenerationsAreUnique_AcrossSessions()
    {
        using var tracker = new SessionBackgroundTaskTracker();

        var handle1 = tracker.BeginSession("session-1", CancellationToken.None);
        var handle2 = tracker.BeginSession("session-2", CancellationToken.None);
        var handle3 = tracker.BeginSession("session-1", CancellationToken.None);

        Assert.AreNotEqual(handle1.Generation, handle2.Generation);
        Assert.AreNotEqual(handle2.Generation, handle3.Generation);
        Assert.AreNotEqual(handle1.Generation, handle3.Generation);
    }

    [TestMethod]
    public void IndependentSessions_DoNotInterfere()
    {
        using var tracker = new SessionBackgroundTaskTracker();
        var handle1 = tracker.BeginSession("session-1", CancellationToken.None);
        var handle2 = tracker.BeginSession("session-2", CancellationToken.None);

        tracker.EndSession("session-1", handle1.Generation);

        Assert.IsFalse(tracker.HasActiveUserLoop("session-1"));
        Assert.IsTrue(tracker.HasActiveUserLoop("session-2"));
    }
}
