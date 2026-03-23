using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Subagent;

/// <summary>
/// Buffers <see cref="SubagentResultMessage"/> objects per batch and coordinates
/// which handler invocation performs the consolidated synthesis.
/// </summary>
internal sealed class SubagentResultGate(ILogger<SubagentResultGate> logger)
{
    private readonly ConcurrentDictionary<string, PendingBatch> _pending = new();

    /// <summary>
    /// Accumulates a subagent result into its batch. Returns:
    /// <list type="bullet">
    ///   <item>A non-null list → this caller should perform consolidated synthesis with these results.</item>
    ///   <item>Null → another caller is synthesizing; just return.</item>
    /// </list>
    /// </summary>
    public async Task<IReadOnlyList<SubagentResultMessage>?> AccumulateAsync(
        SubagentResultMessage result,
        ISubagentManager subagentManager,
        int timeoutSeconds,
        CancellationToken ct)
    {
        // No batchId or consolidate=false → solo synthesis
        if (result.BatchId is null || !result.Consolidate)
            return [result];

        var batchKey = $"{result.PrimarySessionId}:{result.BatchId}";

        var batch = _pending.GetOrAdd(batchKey, _ => new PendingBatch());

        // If a stale batch (already fired > 30s ago), replace it
        if (batch.Fired && batch.FiredAt is { } firedAt
            && DateTimeOffset.UtcNow - firedAt > TimeSpan.FromSeconds(30))
        {
            var fresh = new PendingBatch();
            if (_pending.TryUpdate(batchKey, fresh, batch))
                batch = fresh;
            else
                batch = _pending.GetOrAdd(batchKey, _ => new PendingBatch());
        }

        lock (batch.Lock)
        {
            if (batch.Fired)
            {
                // Late arrival — already fired
                if (batch.Results.Any(r => r.TaskId == result.TaskId))
                    return null; // already included
                logger.LogInformation(
                    "Late arrival for batch {BatchKey} task {TaskId} — solo synthesis",
                    batchKey, result.TaskId);
                return [result]; // solo synthesis
            }

            batch.Results.Add(result);
        }

        // Check for active siblings
        var activeSiblings = subagentManager.ListActive()
            .Where(e => e.PrimarySessionId == result.PrimarySessionId
                     && e.BatchId == result.BatchId
                     && e.Consolidate
                     && e.TaskId != result.TaskId)
            .ToList();

        if (activeSiblings.Count == 0)
        {
            // No active siblings — try to fire immediately
            var fired = TryFire(batch);
            if (fired is not null)
            {
                logger.LogInformation(
                    "Batch {BatchKey} fired immediately with {Count} result(s)",
                    batchKey, fired.Count);
                CleanupBatch(batchKey);
                return fired;
            }
            return null;
        }

        // Active siblings exist — wait for signal, timeout, or cancellation
        logger.LogInformation(
            "Batch {BatchKey} waiting for {SiblingCount} active sibling(s) (timeout={Timeout}s)",
            batchKey, activeSiblings.Count, timeoutSeconds);

        try
        {
            await batch.Signal.Task.WaitAsync(
                TimeSpan.FromSeconds(timeoutSeconds), ct);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Batch {BatchKey} timed out waiting for siblings", batchKey);
        }
        catch (OperationCanceledException)
        {
            // Handler CT cancelled (e.g. new user message) — let the caller exit
            throw;
        }

        // Try to fire (we may or may not win the race)
        var results = TryFire(batch);
        if (results is not null)
        {
            logger.LogInformation(
                "Batch {BatchKey} fired after wait with {Count} result(s)",
                batchKey, results.Count);
            CleanupBatch(batchKey);
            return results;
        }

        return null; // someone else fired
    }

    private static IReadOnlyList<SubagentResultMessage>? TryFire(PendingBatch batch)
    {
        lock (batch.Lock)
        {
            if (batch.Fired) return null;
            batch.Fired = true;
            batch.FiredAt = DateTimeOffset.UtcNow;
            batch.Signal.TrySetResult(true);
            return batch.Results.ToList();
        }
    }

    private void CleanupBatch(string batchKey)
    {
        // Don't remove immediately — keep for late-arrival detection (30s staleness window)
        _ = Task.Delay(TimeSpan.FromSeconds(35)).ContinueWith(_ => _pending.TryRemove(batchKey, out PendingBatch? _));
    }

    private sealed class PendingBatch
    {
        public List<SubagentResultMessage> Results { get; } = [];
        public bool Fired { get; set; }
        public DateTimeOffset? FiredAt { get; set; }
        public Lock Lock { get; } = new();
        public TaskCompletionSource<bool> Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
