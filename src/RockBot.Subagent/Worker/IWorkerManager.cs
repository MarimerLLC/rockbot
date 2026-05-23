namespace RockBot.Subagent.Worker;

/// <summary>
/// Manages worker batches — the lean rung between wisps and subagents. Workers run
/// synchronously within a batch (caller awaits the whole batch), gated by a
/// configurable concurrency limit.
/// </summary>
public interface IWorkerManager
{
    /// <summary>
    /// Spawns one or more workers concurrently (up to
    /// <see cref="WorkerOptions.MaxConcurrentWorkers"/>) and awaits the entire
    /// batch. Returns a typed receipt — the spawning agent reads each result's
    /// <see cref="WorkerResult.ResultKey"/> to retrieve the actual findings from
    /// working memory.
    /// </summary>
    Task<WorkerBatchResult> SpawnBatchAsync(
        IReadOnlyList<WorkerDefinition> definitions,
        string primarySessionId,
        CancellationToken ct);
}
