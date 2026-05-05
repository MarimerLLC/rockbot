using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Subagent;

/// <summary>
/// Buffers <see cref="SubagentResultMessage"/> objects per batch and coordinates
/// which handler invocation performs the consolidated synthesis.
/// </summary>
internal sealed class SubagentResultGate(
    IOptions<SubagentOptions> options,
    ILogger<SubagentResultGate> logger)
{
    private readonly ConcurrentDictionary<string, PendingBatch> _pending = new();

    // Re-checked each tick of the wait loop. Short enough that ListActive() polling
    // catches stragglers promptly without burning CPU.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // After cancelling stragglers at the ceiling, give their failure-result messages
    // this long to land in the batch before we synthesize. The runner publishes the
    // result before returning, but message dispatch through RabbitMQ is async.
    private static readonly TimeSpan CancellationGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Accumulates a subagent result into its batch. Returns:
    /// <list type="bullet">
    ///   <item>A non-null list → this caller should perform consolidated synthesis with these results.</item>
    ///   <item>Null → another caller is synthesizing, OR this is a late arrival to an already-fired batch.</item>
    /// </list>
    /// </summary>
    public async Task<IReadOnlyList<SubagentResultMessage>?> AccumulateAsync(
        SubagentResultMessage result,
        ISubagentManager subagentManager,
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
                // Late arrival — already fired. Drop silently; the late result's Phase 1
                // completion bubble has already been published by the handler. Returning
                // null prevents a duplicate solo synthesis.
                logger.LogInformation(
                    "Late arrival for batch {BatchKey} task {TaskId} — dropping (already fired)",
                    batchKey, result.TaskId);
                return null;
            }

            batch.Results.Add(result);
            // Wake any sibling that's waiting on a poll tick so it can re-check ListActive().
            batch.Signal.TrySetResult(true);
        }

        var ceiling = ChooseCeiling(result.PrimarySessionId);
        var deadline = DateTimeOffset.UtcNow + ceiling;

        // Wait loop: poll ListActive() until no siblings remain, the signal fires, or
        // we hit the ceiling. Each iteration gets a fresh signal so we can detect new
        // arrivals without racing on the previous TaskCompletionSource.
        while (DateTimeOffset.UtcNow < deadline)
        {
            var activeSiblings = subagentManager.ListActive()
                .Where(e => e.PrimarySessionId == result.PrimarySessionId
                         && e.BatchId == result.BatchId
                         && e.Consolidate
                         && e.TaskId != result.TaskId)
                .ToList();

            if (activeSiblings.Count == 0)
            {
                var fired = TryFire(batch);
                if (fired is not null)
                {
                    logger.LogInformation(
                        "Batch {BatchKey} fired with {Count} result(s)", batchKey, fired.Count);
                    CleanupBatch(batchKey);
                    return fired;
                }
                return null; // someone else won the race
            }

            // Reset the signal so we can detect a new arrival during this tick.
            Task signalTask;
            lock (batch.Lock)
            {
                if (batch.Signal.Task.IsCompleted)
                    batch.Signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                signalTask = batch.Signal.Task;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            var waitFor = remaining < PollInterval ? remaining : PollInterval;
            if (waitFor <= TimeSpan.Zero) break;

            try
            {
                await signalTask.WaitAsync(waitFor, ct);
            }
            catch (TimeoutException)
            {
                // Tick elapsed — loop and re-check ListActive().
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        // Ceiling reached. Cancel stragglers and give their failure results time to arrive.
        var stragglers = subagentManager.ListActive()
            .Where(e => e.PrimarySessionId == result.PrimarySessionId
                     && e.BatchId == result.BatchId
                     && e.Consolidate
                     && e.TaskId != result.TaskId)
            .ToList();

        if (stragglers.Count > 0)
        {
            logger.LogWarning(
                "Batch {BatchKey} reached ceiling {Ceiling}s with {Count} active sibling(s) — cancelling",
                batchKey, ceiling.TotalSeconds, stragglers.Count);

            foreach (var s in stragglers)
            {
                try { await subagentManager.CancelAsync(s.TaskId); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to cancel straggler subagent {TaskId}", s.TaskId);
                }
            }

            // Wait for cancellation results to arrive. The runner publishes a failure
            // SubagentResultMessage when its CT fires; another handler invocation will
            // run AccumulateAsync and add it to batch.Results.
            try { await Task.Delay(CancellationGrace, ct); }
            catch (OperationCanceledException) { throw; }
        }

        // Inject synthetic cancellation entries for any straggler whose failure result
        // didn't make it into the batch in time. Guarantees Phase 2 sees every cancelled
        // sibling, even if the message dispatch was slow.
        lock (batch.Lock)
        {
            foreach (var s in stragglers)
            {
                if (batch.Results.Any(r => r.TaskId == s.TaskId)) continue;
                batch.Results.Add(new SubagentResultMessage
                {
                    TaskId = s.TaskId,
                    SubagentSessionId = string.Empty,
                    PrimarySessionId = result.PrimarySessionId,
                    BatchId = result.BatchId,
                    Consolidate = result.Consolidate,
                    IsSuccess = false,
                    Error = "cancelled at consolidation ceiling",
                    Output = $"Subagent {s.TaskId} did not complete within the consolidation ceiling " +
                             $"({ceiling.TotalSeconds:F0}s) and was cancelled.",
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
        }

        var ceilingFired = TryFire(batch);
        if (ceilingFired is not null)
        {
            logger.LogInformation(
                "Batch {BatchKey} fired at ceiling with {Count} result(s) ({Cancelled} cancelled)",
                batchKey, ceilingFired.Count, stragglers.Count);
            CleanupBatch(batchKey);
            return ceilingFired;
        }

        return null; // someone else fired
    }

    private TimeSpan ChooseCeiling(string primarySessionId)
    {
        var opts = options.Value;
        var seconds = primarySessionId.StartsWith("session/", StringComparison.OrdinalIgnoreCase)
            ? opts.InteractiveConsolidationTimeoutSeconds
            : opts.BackgroundConsolidationTimeoutSeconds;
        return TimeSpan.FromSeconds(seconds);
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
        public TaskCompletionSource<bool> Signal { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
