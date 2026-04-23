using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A.Tests;

[TestClass]
public class A2ATaskCancellerTests
{
    private static AgentIdentity TestIdentity => new("primary");

    private static A2ATaskCanceller Build(A2ATaskTracker tracker, TrackingPublisher publisher) =>
        new(tracker, publisher, new A2AOptions(), TestIdentity,
            NullLogger<A2ATaskCanceller>.Instance);

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
    public async Task CancelForSessionAsync_NoMatch_ReturnsZero_AndPublishesNothing()
    {
        var tracker = new A2ATaskTracker();
        var publisher = new TrackingPublisher();
        tracker.Track(MakeTask("t1", "foragent", "wisp-other"));

        var canceller = Build(tracker, publisher);
        var count = await canceller.CancelForSessionAsync("wisp-missing", "test", CancellationToken.None);

        Assert.AreEqual(0, count);
        Assert.AreEqual(0, publisher.Published.Count);
        Assert.AreEqual(1, tracker.ListActive().Count);
    }

    [TestMethod]
    public async Task CancelForSessionAsync_MatchingSession_CancelsCtsRemovesAndPublishes()
    {
        var tracker = new A2ATaskTracker();
        var publisher = new TrackingPublisher();
        var task1 = MakeTask("t1", "foragent", "wisp-1");
        var task2 = MakeTask("t2", "otheragent", "wisp-1");
        var task3 = MakeTask("t3", "foragent", "wisp-2"); // unrelated session
        tracker.Track(task1);
        tracker.Track(task2);
        tracker.Track(task3);

        var canceller = Build(tracker, publisher);
        var count = await canceller.CancelForSessionAsync("wisp-1", "test-abort", CancellationToken.None);

        Assert.AreEqual(2, count);
        Assert.IsTrue(task1.Cts.IsCancellationRequested);
        Assert.IsTrue(task2.Cts.IsCancellationRequested);
        Assert.IsFalse(task3.Cts.IsCancellationRequested);

        // Unrelated task still tracked
        var remaining = tracker.ListActive();
        Assert.AreEqual(1, remaining.Count);
        Assert.AreEqual("t3", remaining[0].TaskId);

        // Cancel published on per-target topics
        Assert.AreEqual(2, publisher.Published.Count);
        var topics = publisher.Published.Select(p => p.Topic).OrderBy(t => t).ToList();
        CollectionAssert.AreEquivalent(
            new[] { "agent.task.cancel.foragent", "agent.task.cancel.otheragent" },
            topics);

        // Correlation ids equal the task ids
        foreach (var (topic, envelope) in publisher.Published)
        {
            var payload = envelope.GetPayload<AgentTaskCancelRequest>();
            Assert.IsNotNull(payload);
            Assert.AreEqual(envelope.CorrelationId, payload.TaskId);
        }
    }

    [TestMethod]
    public async Task CancelForSessionAsync_EmptySessionId_ReturnsZero()
    {
        var tracker = new A2ATaskTracker();
        var publisher = new TrackingPublisher();
        tracker.Track(MakeTask("t1", "foragent", "wisp-1"));

        var canceller = Build(tracker, publisher);
        var count = await canceller.CancelForSessionAsync("", "test", CancellationToken.None);

        Assert.AreEqual(0, count);
        Assert.AreEqual(1, tracker.ListActive().Count);
    }
}
