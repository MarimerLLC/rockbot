using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Subagent.Worker;

namespace RockBot.Subagent.Tests.Worker;

[TestClass]
public class WorkerManagerTests
{
    [TestMethod]
    public async Task SpawnBatchAsync_EmptyBatch_ReturnsZeroResults()
    {
        var manager = CreateManager(maxConcurrent: 3, runnerFactory: () => new InstantRunner());
        var batch = await manager.SpawnBatchAsync([], "session-1", CancellationToken.None);

        Assert.AreEqual(0, batch.TotalCount);
        Assert.AreEqual(0, batch.SucceededCount);
    }

    [TestMethod]
    public async Task SpawnBatchAsync_AllDefinitionsRun_ResultsInSubmissionOrder()
    {
        var manager = CreateManager(maxConcurrent: 3, runnerFactory: () => new InstantRunner());
        var definitions = new List<WorkerDefinition>
        {
            new() { Description = "task A" },
            new() { Description = "task B", ResultKey = "shared/custom-key" },
            new() { Description = "task C" },
        };

        var batch = await manager.SpawnBatchAsync(definitions, "session-1", CancellationToken.None);

        Assert.AreEqual(3, batch.TotalCount);
        Assert.AreEqual(3, batch.SucceededCount);
        Assert.AreEqual("shared/custom-key", batch.Results[1].ResultKey);
        StringAssert.StartsWith(batch.BatchId, "worker-batch-");
    }

    [TestMethod]
    public async Task SpawnBatchAsync_RespectsMaxConcurrentWorkers()
    {
        var tracker = new ConcurrencyTracker();
        var manager = CreateManager(
            maxConcurrent: 2,
            runnerFactory: () => new TrackingRunner(tracker, holdMs: 60));

        var definitions = Enumerable.Range(0, 6)
            .Select(i => new WorkerDefinition { Description = $"task {i}" })
            .ToList();

        var batch = await manager.SpawnBatchAsync(definitions, "session-1", CancellationToken.None);

        Assert.AreEqual(6, batch.TotalCount);
        Assert.AreEqual(6, batch.SucceededCount);
        Assert.IsTrue(tracker.PeakConcurrency <= 2,
            $"Expected peak concurrency ≤2 (semaphore cap), got {tracker.PeakConcurrency}");
        Assert.IsTrue(tracker.PeakConcurrency >= 1, "Expected at least one worker to run");
    }

    [TestMethod]
    public async Task SpawnBatchAsync_OversizeBatch_StillCompletesByQueueing()
    {
        // Batch with more definitions than MaxConcurrentWorkers — overflow queues
        // instead of failing the batch.
        var manager = CreateManager(maxConcurrent: 2, runnerFactory: () => new InstantRunner());
        var definitions = Enumerable.Range(0, 5)
            .Select(i => new WorkerDefinition { Description = $"task {i}" })
            .ToList();

        var batch = await manager.SpawnBatchAsync(definitions, "session-1", CancellationToken.None);

        Assert.AreEqual(5, batch.TotalCount);
        Assert.AreEqual(5, batch.SucceededCount);
    }

    [TestMethod]
    public async Task SpawnBatchAsync_RunnerThrows_ResultMarkedFailed()
    {
        var manager = CreateManager(
            maxConcurrent: 2,
            runnerFactory: () => new ThrowingRunner());

        var batch = await manager.SpawnBatchAsync(
            [new WorkerDefinition { Description = "broken task" }],
            "session-1", CancellationToken.None);

        Assert.AreEqual(1, batch.TotalCount);
        Assert.AreEqual(0, batch.SucceededCount);
        Assert.AreEqual(1, batch.FailedCount);
        StringAssert.Contains(batch.Results[0].FailureReason!, "manager exception");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static WorkerManager CreateManager(int maxConcurrent, Func<IWorkerRunner> runnerFactory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient(_ => runnerFactory());

        var provider = services.BuildServiceProvider();

        var opts = Options.Create(new WorkerOptions { MaxConcurrentWorkers = maxConcurrent });
        return new WorkerManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            opts,
            NullLogger<WorkerManager>.Instance);
    }

    private sealed class InstantRunner : IWorkerRunner
    {
        public Task<WorkerResult> RunAsync(string taskId, WorkerDefinition definition,
            string primarySessionId, string batchId, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new WorkerResult
            {
                TaskId = taskId,
                IsSuccess = true,
                ResultKey = definition.ResultKey ?? $"worker/{taskId}/result",
                Duration = TimeSpan.FromMilliseconds(1),
                LlmTurns = 1,
            });
    }

    private sealed class ThrowingRunner : IWorkerRunner
    {
        public Task<WorkerResult> RunAsync(string taskId, WorkerDefinition definition,
            string primarySessionId, string batchId, TimeSpan timeout, CancellationToken ct) =>
            throw new InvalidOperationException("test exception");
    }

    private sealed class ConcurrencyTracker
    {
        private int _current;
        public int PeakConcurrency { get; private set; }
        private readonly object _lock = new();

        public void Enter()
        {
            lock (_lock)
            {
                _current++;
                if (_current > PeakConcurrency) PeakConcurrency = _current;
            }
        }

        public void Exit()
        {
            lock (_lock) _current--;
        }
    }

    private sealed class TrackingRunner(ConcurrencyTracker tracker, int holdMs) : IWorkerRunner
    {
        public async Task<WorkerResult> RunAsync(string taskId, WorkerDefinition definition,
            string primarySessionId, string batchId, TimeSpan timeout, CancellationToken ct)
        {
            tracker.Enter();
            try
            {
                await Task.Delay(holdMs, ct);
            }
            finally
            {
                tracker.Exit();
            }
            return new WorkerResult
            {
                TaskId = taskId,
                IsSuccess = true,
                ResultKey = $"worker/{taskId}/result",
                Duration = TimeSpan.FromMilliseconds(holdMs),
            };
        }
    }
}
