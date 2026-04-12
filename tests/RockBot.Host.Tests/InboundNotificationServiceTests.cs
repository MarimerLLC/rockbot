using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Host.Tests;

[TestClass]
public class InboundNotificationServiceTests
{
    private TestNotificationQueue _queue = null!;
    private TestActivityMonitor _activityMonitor = null!;
    private TestSessionTracker _sessionTracker = null!;
    private TestPublisher _publisher = null!;
    private InboundNotificationService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _queue = new TestNotificationQueue();
        _activityMonitor = new TestActivityMonitor();
        _sessionTracker = new TestSessionTracker();
        _publisher = new TestPublisher();
        _service = new InboundNotificationService(
            _queue,
            _activityMonitor,
            _sessionTracker,
            _publisher,
            new AgentIdentity("RockBot"),
            NullLogger<InboundNotificationService>.Instance);
    }

    [TestCleanup]
    public void Cleanup() => _service.Dispose();

    // ── skip conditions ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Flush_SkipsWhenQueueEmpty()
    {
        _activityMonitor.IsActive = false;
        _sessionTracker.ActiveLoop = false;

        InvokeCheckAndFlush();
        await Task.Delay(50);

        Assert.AreEqual(0, _publisher.Published.Count);
    }

    [TestMethod]
    public async Task Flush_SkipsWhenUserHasActiveLoop()
    {
        _sessionTracker.ActiveLoop = true;
        _activityMonitor.IsActive = false;
        await _queue.EnqueueAsync(CreateNotification("t1", "Agent1"), CancellationToken.None);

        InvokeCheckAndFlush();
        await Task.Delay(50);

        Assert.AreEqual(0, _publisher.Published.Count);
        Assert.AreEqual(1, _queue.PendingCount, "Queue should not be drained");
    }

    [TestMethod]
    public async Task Flush_SkipsWhenUserRecentlyActive()
    {
        _sessionTracker.ActiveLoop = false;
        _activityMonitor.IsActive = true;
        await _queue.EnqueueAsync(CreateNotification("t1", "Agent1"), CancellationToken.None);

        InvokeCheckAndFlush();
        await Task.Delay(50);

        Assert.AreEqual(0, _publisher.Published.Count);
        Assert.AreEqual(1, _queue.PendingCount, "Queue should not be drained");
    }

    // ── flush behavior ───────────────────────────────────────────────────

    [TestMethod]
    public async Task Flush_DeliversWhenUserIdle()
    {
        _sessionTracker.ActiveLoop = false;
        _activityMonitor.IsActive = false;
        await _queue.EnqueueAsync(CreateNotification("t1", "Agent1"), CancellationToken.None);

        InvokeCheckAndFlush();
        await _publisher.WaitForPublish();

        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual($"{UserProxyTopics.UserResponse}.RockBot", _publisher.Published[0].Topic);

        var reply = _publisher.Published[0].Envelope.GetPayload<AgentReply>();
        Assert.IsNotNull(reply);
        Assert.IsTrue(reply.IsFinal);
        Assert.AreEqual(InboundNotificationService.A2AInboundSessionId, reply.SessionId);
        Assert.AreEqual(InboundNotificationService.A2AInboundAgentName, reply.AgentName);
    }

    [TestMethod]
    public async Task Flush_DrainsQueue()
    {
        _sessionTracker.ActiveLoop = false;
        _activityMonitor.IsActive = false;
        await _queue.EnqueueAsync(CreateNotification("t1", "Agent1"), CancellationToken.None);
        await _queue.EnqueueAsync(CreateNotification("t2", "Agent2"), CancellationToken.None);

        InvokeCheckAndFlush();
        await _publisher.WaitForPublish();

        Assert.AreEqual(0, _queue.PendingCount, "Queue should be drained after flush");
    }

    // ── format: single notification ──────────────────────────────────────

    [TestMethod]
    public async Task Flush_FormatsSingleNotification()
    {
        _sessionTracker.ActiveLoop = false;
        _activityMonitor.IsActive = false;
        await _queue.EnqueueAsync(new InboundNotification
        {
            TaskId = "t1",
            CallerName = "HelperBot",
            Summary = "Wants to sync calendars",
            ReceivedAt = DateTimeOffset.UtcNow,
            SkillId = "calendar-sync"
        }, CancellationToken.None);

        InvokeCheckAndFlush();
        await _publisher.WaitForPublish();

        var reply = _publisher.Published[0].Envelope.GetPayload<AgentReply>()!;
        StringAssert.Contains(reply.Content, "HelperBot");
        StringAssert.Contains(reply.Content, "calendar-sync");
        StringAssert.Contains(reply.Content, "Wants to sync calendars");
    }

    // ── format: multiple notifications (batch) ───────────────────────────

    [TestMethod]
    public async Task Flush_FormatsMultipleNotifications()
    {
        _sessionTracker.ActiveLoop = false;
        _activityMonitor.IsActive = false;
        await _queue.EnqueueAsync(CreateNotification("t1", "Bot1", "First summary"), CancellationToken.None);
        await _queue.EnqueueAsync(CreateNotification("t2", "Bot2", "Second summary"), CancellationToken.None);

        InvokeCheckAndFlush();
        await _publisher.WaitForPublish();

        var reply = _publisher.Published[0].Envelope.GetPayload<AgentReply>()!;
        StringAssert.Contains(reply.Content, "2 agents reached out");
        StringAssert.Contains(reply.Content, "Bot1");
        StringAssert.Contains(reply.Content, "Bot2");
        StringAssert.Contains(reply.Content, "First summary");
        StringAssert.Contains(reply.Content, "Second summary");
    }

    // ── cancellation ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task Flush_RespectsStopCancellation()
    {
        _sessionTracker.ActiveLoop = false;
        _activityMonitor.IsActive = false;
        await _queue.EnqueueAsync(CreateNotification("t1", "Agent1"), CancellationToken.None);

        // Stop the service before flushing — sets the CancellationToken
        await _service.StopAsync(CancellationToken.None);

        // Now try to flush — it should catch the OperationCanceledException
        InvokeCheckAndFlush();
        await Task.Delay(50);

        // Publisher should not receive anything because the token was cancelled
        Assert.AreEqual(0, _publisher.Published.Count);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private void InvokeCheckAndFlush()
    {
        var method = typeof(InboundNotificationService).GetMethod(
            "CheckAndFlush", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(_service, [null]);
    }

    private static InboundNotification CreateNotification(
        string taskId, string callerName, string? summary = null) =>
        new()
        {
            TaskId = taskId,
            CallerName = callerName,
            Summary = summary ?? $"Test notification from {callerName}",
            ReceivedAt = DateTimeOffset.UtcNow
        };

    // ── test doubles ─────────────────────────────────────────────────────

    private sealed class TestNotificationQueue : IInboundNotificationQueue
    {
        private readonly List<InboundNotification> _items = [];
        public int PendingCount => _items.Count;

        public Task EnqueueAsync(InboundNotification notification, CancellationToken ct)
        {
            _items.Add(notification);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<InboundNotification>> DrainAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var items = _items.ToList();
            _items.Clear();
            return Task.FromResult<IReadOnlyList<InboundNotification>>(items);
        }
    }

    private sealed class TestActivityMonitor : IUserActivityMonitor
    {
        public bool IsActive { get; set; }
        public void RecordActivity() { }
        public bool IsUserActive(TimeSpan idleThreshold) => IsActive;
    }

    private sealed class TestSessionTracker : ISessionTracker
    {
        public bool ActiveLoop { get; set; }
        public SessionHandle BeginSession(string sessionId, CancellationToken hostCt) => new(CancellationToken.None, 0);
        public void EndSession(string sessionId, long generation) { }
        public bool HasActiveUserLoop(string sessionId) => ActiveLoop;
    }

    private sealed class TestPublisher : IMessagePublisher
    {
        private readonly TaskCompletionSource _tcs = new();
        public List<(string Topic, MessageEnvelope Envelope)> Published { get; } = [];

        public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken ct = default)
        {
            Published.Add((topic, envelope));
            _tcs.TrySetResult();
            return Task.CompletedTask;
        }

        public Task WaitForPublish(TimeSpan? timeout = null) =>
            _tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
