using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.A2A.Tests;

[TestClass]
public class A2ATaskAwaiterTests
{
    private static A2ATaskAwaiter Build(A2ATaskTracker tracker) =>
        new(tracker, NullLogger<A2ATaskAwaiter>.Instance);

    private static PendingA2ATask MakeTask(string taskId, string targetAgent, string sessionId) =>
        new()
        {
            TaskId = taskId,
            TargetAgent = targetAgent,
            Skill = "skill",
            PrimarySessionId = sessionId,
            StartedAt = DateTimeOffset.UtcNow,
            Cts = new CancellationTokenSource()
        };

    [TestMethod]
    public async Task WaitForSessionAsync_NoPendingTasks_ReturnsZeroImmediately()
    {
        var tracker = new A2ATaskTracker();
        var awaiter = Build(tracker);

        var count = await awaiter.WaitForSessionAsync("subagent/none", CancellationToken.None);

        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task WaitForSessionAsync_EmptySessionId_ReturnsZero()
    {
        var tracker = new A2ATaskTracker();
        tracker.Track(MakeTask("t1", "agent", "subagent/s1"));
        var awaiter = Build(tracker);

        var count = await awaiter.WaitForSessionAsync("", CancellationToken.None);

        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task WaitForSessionAsync_OnlyOtherSessionsPending_ReturnsZero()
    {
        var tracker = new A2ATaskTracker();
        tracker.Track(MakeTask("t1", "agent", "subagent/other"));
        var awaiter = Build(tracker);

        var count = await awaiter.WaitForSessionAsync("subagent/mine", CancellationToken.None);

        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task WaitForSessionAsync_ReturnsWhenTrackerEmpties()
    {
        var tracker = new A2ATaskTracker();
        tracker.Track(MakeTask("t1", "agent", "subagent/s1"));
        var awaiter = Build(tracker);

        // Remove the task shortly after the wait begins, simulating an A2A result arriving.
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            tracker.TryRemove("t1", out _);
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = await awaiter.WaitForSessionAsync("subagent/s1", CancellationToken.None);
        sw.Stop();

        Assert.AreEqual(1, count);
        // Polling interval is 250ms; allow generous slack for CI scheduling jitter.
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"awaiter took {sw.Elapsed} — should return shortly after tracker empties");
    }

    [TestMethod]
    public async Task WaitForSessionAsync_OnlyAwaitsMatchingSession()
    {
        var tracker = new A2ATaskTracker();
        tracker.Track(MakeTask("t1", "agent", "subagent/mine"));
        tracker.Track(MakeTask("t2", "agent", "subagent/sibling")); // unrelated, stays pending
        var awaiter = Build(tracker);

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            tracker.TryRemove("t1", out _);
        });

        var count = await awaiter.WaitForSessionAsync("subagent/mine", CancellationToken.None);

        Assert.AreEqual(1, count);
        // Sibling-session task should still be tracked — the awaiter must not touch it.
        Assert.AreEqual(1, tracker.ListBySession("subagent/sibling").Count);
    }

    [TestMethod]
    public async Task WaitForSessionAsync_CancelledToken_StopsWaitingButReturnsInitialCount()
    {
        var tracker = new A2ATaskTracker();
        tracker.Track(MakeTask("t1", "agent", "subagent/s1"));
        tracker.Track(MakeTask("t2", "agent", "subagent/s1"));
        var awaiter = Build(tracker);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var count = await awaiter.WaitForSessionAsync("subagent/s1", cts.Token);

        // Returns initial count even when cancelled — callers see what they signed up to wait for.
        Assert.AreEqual(2, count);
        // Tasks remain in the tracker; nothing happened to them.
        Assert.AreEqual(2, tracker.ListBySession("subagent/s1").Count);
    }
}
