using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Subagent.Worker;

/// <summary>
/// Singleton manager that batches worker execution behind a concurrency gate.
/// Each worker is resolved out of its own DI scope so per-worker dependencies
/// (like the worker runner's per-task tool wiring) can be transient.
/// </summary>
public sealed class WorkerManager(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<WorkerManager> logger) : IWorkerManager
{
    public async Task<WorkerBatchResult> SpawnBatchAsync(
        IReadOnlyList<WorkerDefinition> definitions,
        string primarySessionId,
        CancellationToken ct)
    {
        if (definitions.Count == 0)
        {
            return new WorkerBatchResult
            {
                BatchId = $"worker-batch-{Guid.NewGuid():N}"[..20],
                Results = [],
                TotalDuration = TimeSpan.Zero,
            };
        }

        var opts = options.Value;
        if (definitions.Count > opts.MaxConcurrentWorkers)
        {
            // We still execute every definition — the semaphore queues the overflow.
            // Log a warning so this shows up in telemetry if it happens often.
            logger.LogWarning(
                "Worker batch of {Count} exceeds MaxConcurrentWorkers={Max}; overflow will queue",
                definitions.Count, opts.MaxConcurrentWorkers);
        }

        var batchId = $"worker-batch-{Guid.NewGuid():N}"[..20];
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var semaphore = new SemaphoreSlim(opts.MaxConcurrentWorkers);

        var tasks = definitions
            .Select(def => RunOneAsync(def, batchId, primarySessionId, opts, semaphore, ct))
            .ToList();
        var results = await Task.WhenAll(tasks);

        sw.Stop();

        logger.LogInformation(
            "Worker batch {BatchId} complete — {Total} worker(s), {Succeeded} ok, {Failed} failed, {ElapsedMs}ms",
            batchId, results.Length, results.Count(r => r.IsSuccess),
            results.Count(r => !r.IsSuccess), sw.ElapsedMilliseconds);

        return new WorkerBatchResult
        {
            BatchId = batchId,
            Results = results,
            TotalDuration = sw.Elapsed,
        };
    }

    private async Task<WorkerResult> RunOneAsync(
        WorkerDefinition definition,
        string batchId,
        string primarySessionId,
        WorkerOptions opts,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var taskId = Guid.NewGuid().ToString("N")[..12];
            var timeoutMin = definition.TimeoutMinutes ?? opts.DefaultTimeoutMinutes;
            var timeout = TimeSpan.FromMinutes(timeoutMin);

            // Do NOT link the caller's ct as a child of a wider session cancel —
            // workers are batched, the caller is already awaiting Task.WhenAll, so
            // the caller's ct IS the appropriate signal. We layer on a timeout CTS.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var runner = scope.ServiceProvider.GetRequiredService<IWorkerRunner>();
                return await runner.RunAsync(
                    taskId, definition, primarySessionId, batchId, timeout, cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Worker {TaskId} failed before runner could return a result", taskId);
                var resultKey = definition.ResultKey ?? $"worker/{taskId}/result";
                return new WorkerResult
                {
                    TaskId = taskId,
                    IsSuccess = false,
                    ResultKey = resultKey,
                    FactsRecorded = 0,
                    Blocked = [],
                    ConvergedPatterns = [],
                    Duration = TimeSpan.Zero,
                    LlmTurns = 0,
                    FailureReason = $"manager exception: {ex.Message}",
                };
            }
        }
        finally
        {
            semaphore.Release();
        }
    }
}
