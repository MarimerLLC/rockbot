using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

[TestClass]
public sealed class SchedulerServiceTests
{
    private static AgentClock MakeClock() =>
        new(new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()),
            NullLogger<AgentClock>.Instance);

    private static SchedulerService MakeService(
        IScheduledTaskStore store,
        IMessagePipeline pipeline) =>
        new(store,
            pipeline,
            MakeClock(),
            new AgentIdentity("test-agent"),
            NullLogger<SchedulerService>.Instance);

    // ── ListAsync ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ListAsync_DelegatesToStore()
    {
        var task = new ScheduledTask("t1", "0 8 * * *", "desc", DateTimeOffset.UtcNow);
        var store = new FakeScheduledTaskStore([task]);
        var service = MakeService(store, new NullPipeline());

        var result = await service.ListAsync();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("t1", result[0].Name);
    }

    // ── ScheduleAsync ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ScheduleAsync_SavesTaskToStore()
    {
        var store = new FakeScheduledTaskStore([]);
        var service = MakeService(store, new NullPipeline());

        var task = new ScheduledTask("new-task", "0 9 * * *", "Run report", DateTimeOffset.UtcNow);
        await service.ScheduleAsync(task);

        var saved = await store.GetAsync("new-task");
        Assert.IsNotNull(saved);
        Assert.AreEqual("0 9 * * *", saved.CronExpression);
    }

    // ── CancelAsync ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task CancelAsync_ExistingTask_ReturnsTrueAndDeletesFromStore()
    {
        var task = new ScheduledTask("my-task", "0 8 * * *", "desc", DateTimeOffset.UtcNow);
        var store = new FakeScheduledTaskStore([task]);
        var service = MakeService(store, new NullPipeline());

        var result = await service.CancelAsync("my-task");

        Assert.IsTrue(result);
        Assert.IsNull(await store.GetAsync("my-task"));
    }

    [TestMethod]
    public async Task CancelAsync_UnknownTask_ReturnsFalse()
    {
        var store = new FakeScheduledTaskStore([]);
        var service = MakeService(store, new NullPipeline());

        var result = await service.CancelAsync("nonexistent");
        Assert.IsFalse(result);
    }

    // ── StartAsync / StopAsync ────────────────────────────────────────────────

    [TestMethod]
    public async Task StartAsync_LoadsExistingTasksFromStore()
    {
        // Task that fires far in the future so the timer doesn't fire during the test
        var task = new ScheduledTask("future-task", "0 0 1 1 *", "New Year task", DateTimeOffset.UtcNow);
        var store = new FakeScheduledTaskStore([task]);
        var service = MakeService(store, new NullPipeline());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // ListAsync was called during StartAsync — store should still have the task
        Assert.IsTrue(store.ListCalled);
    }

    [TestMethod]
    public async Task StartAsync_Then_StopAsync_DoesNotThrow()
    {
        var store = new FakeScheduledTaskStore([]);
        var service = MakeService(store, new NullPipeline());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task FiresTask_WhenTimerElapses()
    {
        // Use a 6-field cron with seconds so we can test with a 1-second interval
        var task = new ScheduledTask("quick-task", "* * * * * *", "Fire every second", DateTimeOffset.UtcNow);
        var store = new FakeScheduledTaskStore([task]);
        var pipeline = new CapturingPipeline();
        var service = MakeService(store, pipeline);

        await service.StartAsync(CancellationToken.None);

        // Wait up to 3 seconds for the task to fire
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (pipeline.DispatchCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(pipeline.DispatchCount > 0, "Pipeline should have been dispatched at least once");
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeScheduledTaskStore(IEnumerable<ScheduledTask> initial) : IScheduledTaskStore
    {
        private readonly Dictionary<string, ScheduledTask> _tasks =
            initial.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        public bool ListCalled { get; private set; }

        public Task SaveAsync(ScheduledTask task)
        {
            _tasks[task.Name] = task;
            return Task.CompletedTask;
        }

        public Task<ScheduledTask?> GetAsync(string name) =>
            Task.FromResult(_tasks.GetValueOrDefault(name));

        public Task<IReadOnlyList<ScheduledTask>> ListAsync()
        {
            ListCalled = true;
            return Task.FromResult<IReadOnlyList<ScheduledTask>>(_tasks.Values.ToList());
        }

        public Task<bool> DeleteAsync(string name)
        {
            var removed = _tasks.Remove(name);
            return Task.FromResult(removed);
        }

        public Task UpdateLastFiredAsync(string name, DateTimeOffset firedAt)
        {
            if (_tasks.TryGetValue(name, out var existing))
                _tasks[name] = existing with { LastFiredAt = firedAt };
            return Task.CompletedTask;
        }
    }

    private sealed class NullPipeline : IMessagePipeline
    {
        public Task<MessageResult> DispatchAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
            => Task.FromResult(MessageResult.Ack);
    }

    private sealed class CapturingPipeline : IMessagePipeline
    {
        public int DispatchCount;

        public Task<MessageResult> DispatchAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DispatchCount);
            return Task.FromResult(MessageResult.Ack);
        }
    }
}
