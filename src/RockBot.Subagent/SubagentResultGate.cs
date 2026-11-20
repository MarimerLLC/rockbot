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

    // Extra slack added on top of the consolidation ceiling for the late-arrival window
    // and post-fire batch retention. Phase 2 synthesis can run for tens of seconds, and
    // a stragger result message may sit in the broker through that whole time before
    // being delivered to a fresh handler invocation. We must keep the fired batch around
    // long enough for that late arrival to find it and short-circuit (return null)
    // instead of starting a brand-new batch that fires its own duplicate synthesis.
    private static readonly TimeSpan PostFireRetentionSlack = TimeSpan.FromMinutes(2);

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

        // If a fired batch is "stale enough" to be safely replaced — i.e. older than
        // (consolidation ceiling + slack) — start a fresh one. Within that window we
        // assume any incoming result with this batchId is a delayed sibling of the
        // already-fired batch, not a brand-new batch with a coincidentally reused key.
        // BatchIds are 12-char GUID prefixes per primary session, so true reuse is
        // astronomically unlikely; the staleness check only protects against pathological
        // edge cases (e.g. clock skew on resume after suspend).
        var staleThreshold = ChooseCeiling(result.PrimarySessionId) + PostFireRetentionSlack;
        if (batch.Fired && batch.FiredAt is { } firedAt
            && DateTimeOffset.UtcNow - firedAt > staleThreshold)
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
        var arrivalGrace = TimeSpan.FromSeconds(Math.Max(0, options.Value.ResultArrivalGraceSeconds));

        // Wait loop: poll the manager until no sibling is pending (still running, or finished
        // with its result not yet here), or we hit the ceiling. Each iteration gets a fresh
        // signal so we can detect new arrivals without racing on the previous TaskCompletionSource.
        while (DateTimeOffset.UtcNow < deadline)
        {
            var (activeCount, nextArrivalDeadline) =
                PendingSiblings(result, batch, batchKey, subagentManager, arrivalGrace);

            if (activeCount == 0 && nextArrivalDeadline is null)
            {
                var fired = TryFire(batch);
                if (fired is not null)
                {
                    logger.LogInformation(
                        "Batch {BatchKey} fired with {Count} result(s)", batchKey, fired.Count);
                    CleanupBatch(batchKey, result.PrimarySessionId);
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

            var now = DateTimeOffset.UtcNow;
            var remaining = deadline - now;
            var waitFor = remaining < PollInterval ? remaining : PollInterval;
            // Re-check as soon as a finished sibling's arrival grace runs out.
            if (nextArrivalDeadline is { } arrivalDeadline && arrivalDeadline - now < waitFor)
                waitFor = arrivalDeadline - now;
            if (remaining <= TimeSpan.Zero) break;
            if (waitFor <= TimeSpan.Zero) continue;

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
            .Where(e => IsSibling(result, e.TaskId, e.PrimarySessionId, e.BatchId, e.Consolidate))
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
            CleanupBatch(batchKey, result.PrimarySessionId);
            return ceilingFired;
        }

        return null; // someone else fired
    }

    /// <summary>
    /// The siblings the batch still waits for (#858): those still running, and those that have
    /// finished but whose result has not entered the batch yet. A finished subagent leaves the
    /// manager's active set as soon as it publishes its result, well before the result crosses
    /// the broker and the handler's per-result work to get here; without the second kind, the
    /// first result to arrive fired alone and its siblings were dropped as late arrivals.
    /// Returns the number still running, and the earliest time a finished sibling's arrival
    /// grace runs out (null when none is awaited).
    /// </summary>
    private (int ActiveCount, DateTimeOffset? NextArrivalDeadline) PendingSiblings(
        SubagentResultMessage result,
        PendingBatch batch,
        string batchKey,
        ISubagentManager subagentManager,
        TimeSpan arrivalGrace)
    {
        // Active first: an entry is listed as finished before it leaves the active set, so
        // reading in this order can't miss one moving between the two.
        var activeCount = subagentManager.ListActive().Count(e => IsSibling(result, e.TaskId,
            e.PrimarySessionId, e.BatchId, e.Consolidate));
        var finished = subagentManager.ListRecentlyFinished()
            .Where(f => IsSibling(result, f.TaskId, f.PrimarySessionId, f.BatchId, f.Consolidate))
            .ToList();

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? nextArrivalDeadline = null;
        lock (batch.Lock)
        {
            foreach (var f in finished)
            {
                if (batch.Results.Any(r => r.TaskId == f.TaskId))
                {
                    batch.AwaitedArrivals.Remove(f.TaskId);
                    continue;
                }

                var arrivalDeadline = f.FinishedAt + arrivalGrace;
                if (arrivalDeadline > now)
                {
                    batch.AwaitedArrivals.Add(f.TaskId);
                    if (nextArrivalDeadline is null || arrivalDeadline < nextArrivalDeadline)
                        nextArrivalDeadline = arrivalDeadline;
                }
                else if (batch.AwaitedArrivals.Remove(f.TaskId))
                {
                    logger.LogWarning(
                        "Batch {BatchKey}: sibling {TaskId} finished {Seconds:F0}s ago but its result has not arrived — no longer waiting for it",
                        batchKey, f.TaskId, (now - f.FinishedAt).TotalSeconds);
                }
            }
        }

        return (activeCount, nextArrivalDeadline);
    }

    private static bool IsSibling(
        SubagentResultMessage result, string taskId, string primarySessionId, string? batchId, bool consolidate) =>
        primarySessionId == result.PrimarySessionId
        && batchId == result.BatchId
        && consolidate
        && taskId != result.TaskId;

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

    private void CleanupBatch(string batchKey, string primarySessionId)
    {
        // Keep the fired batch around for at least (consolidation ceiling + slack) so
        // late-arriving sibling results find it and return null (deferred-to-winner)
        // instead of provoking a fresh PendingBatch with a duplicate synthesis. Must
        // outlive both the staleness window in AccumulateAsync and any plausible
        // Phase 2 synthesis time.
        var retention = ChooseCeiling(primarySessionId) + PostFireRetentionSlack + TimeSpan.FromSeconds(5);
        _ = Task.Delay(retention).ContinueWith(_ => _pending.TryRemove(batchKey, out PendingBatch? _));
    }

    private sealed class PendingBatch
    {
        public List<SubagentResultMessage> Results { get; } = [];

        // Finished siblings a waiter has waited on whose result hasn't arrived (#858), so the
        // warning for one that never arrives is logged once, and only for a sibling actually awaited.
        public HashSet<string> AwaitedArrivals { get; } = [];
        public bool Fired { get; set; }
        public DateTimeOffset? FiredAt { get; set; }
        public Lock Lock { get; } = new();
        public TaskCompletionSource<bool> Signal { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
