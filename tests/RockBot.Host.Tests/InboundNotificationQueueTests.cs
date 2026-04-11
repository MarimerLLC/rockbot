using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public class InboundNotificationQueueTests
{
    [TestMethod]
    public async Task Enqueue_IncrementsPendingCount()
    {
        var queue = new InboundNotificationQueue();
        Assert.AreEqual(0, queue.PendingCount);

        await queue.EnqueueAsync(CreateNotification("t1", "Agent1"), CancellationToken.None);

        Assert.AreEqual(1, queue.PendingCount);
    }

    [TestMethod]
    public async Task Drain_ReturnsAllAndClears()
    {
        var queue = new InboundNotificationQueue();
        await queue.EnqueueAsync(CreateNotification("t1", "Agent1"), CancellationToken.None);
        await queue.EnqueueAsync(CreateNotification("t2", "Agent2"), CancellationToken.None);

        var items = await queue.DrainAsync(CancellationToken.None);

        Assert.AreEqual(2, items.Count);
        Assert.AreEqual(0, queue.PendingCount);
    }

    [TestMethod]
    public async Task Drain_WhenEmpty_ReturnsEmptyList()
    {
        var queue = new InboundNotificationQueue();

        var items = await queue.DrainAsync(CancellationToken.None);

        Assert.AreEqual(0, items.Count);
    }

    [TestMethod]
    public async Task Drain_PreservesOrder()
    {
        var queue = new InboundNotificationQueue();
        await queue.EnqueueAsync(CreateNotification("t1", "First"), CancellationToken.None);
        await queue.EnqueueAsync(CreateNotification("t2", "Second"), CancellationToken.None);
        await queue.EnqueueAsync(CreateNotification("t3", "Third"), CancellationToken.None);

        var items = await queue.DrainAsync(CancellationToken.None);

        Assert.AreEqual("First", items[0].CallerName);
        Assert.AreEqual("Second", items[1].CallerName);
        Assert.AreEqual("Third", items[2].CallerName);
    }

    private static InboundNotification CreateNotification(string taskId, string callerName) =>
        new()
        {
            TaskId = taskId,
            CallerName = callerName,
            Summary = $"Test notification from {callerName}",
            ReceivedAt = DateTimeOffset.UtcNow
        };
}
