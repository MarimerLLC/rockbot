namespace RockBot.Host;

/// <summary>
/// Default implementation of <see cref="IAgentWorkSerializer"/>.
/// Uses a single semaphore slot to ensure at most one full agent loop runs
/// at a time, and a preemption <see cref="CancellationTokenSource"/> that is
/// cancelled whenever a user session acquires the slot.
/// </summary>
internal sealed class AgentWorkSerializer : IAgentWorkSerializer, IDisposable
{
    private readonly SemaphoreSlim _slot = new(1, 1);

    // Replaced each time a user loop acquires the slot; cancels any scheduled
    // task that holds the slot at that moment.
    private CancellationTokenSource _preemptCts = new();
    private readonly object _preemptLock = new();

    // Set under _preemptLock at disposal. Scheduled acquisitions check it so a
    // timer callback that outruns host shutdown reports "no slot" instead of
    // throwing ObjectDisposedException out of an unobserved task.
    private bool _disposed;

    // ── User loop ─────────────────────────────────────────────────────────────

    public async Task<IAsyncDisposable> AcquireForUserAsync(CancellationToken ct)
    {
        // Signal any running scheduled task to stop so the slot becomes free.
        CancellationTokenSource newPreempt;
        lock (_preemptLock)
        {
            _preemptCts.Cancel();
            _preemptCts.Dispose();
            newPreempt = _preemptCts = new CancellationTokenSource();
        }

        // Wait for the slot — the preempted task releases it on cancellation.
        await _slot.WaitAsync(ct);

        return new SlotHandle(_slot);
    }

    // ── Scheduled task ────────────────────────────────────────────────────────

    public Task<IScheduledTaskSlot?> TryAcquireForScheduledAsync(CancellationToken ct)
    {
        CancellationToken preemptToken;

        // Both the disposal check and the semaphore wait happen under the lock so
        // Dispose cannot land between them and leave us reading a disposed token.
        // The wait is non-blocking (0 timeout), so holding the lock across it is safe —
        // AcquireForUserAsync does its blocking wait outside the lock.
        lock (_preemptLock)
        {
            // Shutting down. "No slot available" is an outcome every caller already
            // handles, and it is the honest answer once the host is tearing down.
            if (_disposed)
                return Task.FromResult<IScheduledTaskSlot?>(null);

            // Non-blocking: if the slot is held by a user loop, skip this run.
            if (!_slot.Wait(0))
                return Task.FromResult<IScheduledTaskSlot?>(null);

            preemptToken = _preemptCts.Token;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, preemptToken);
        return Task.FromResult<IScheduledTaskSlot?>(new ScheduledTaskSlot(_slot, linked));
    }

    public void Dispose()
    {
        lock (_preemptLock)
        {
            if (_disposed) return;
            _disposed = true;

            _preemptCts.Dispose();
            _slot.Dispose();
        }
    }

    // ── Handles ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Releases the slot, tolerating a serializer that was disposed while the work was
    /// still running. Work that outruns host shutdown has nothing left to hand the slot
    /// back to, so the release is a no-op rather than a failure.
    /// </summary>
    private static void ReleaseQuietly(SemaphoreSlim slot)
    {
        try
        {
            slot.Release();
        }
        catch (ObjectDisposedException)
        {
            // Serializer disposed during shutdown; nobody is waiting on the slot.
        }
    }

    private sealed class SlotHandle(SemaphoreSlim slot) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            ReleaseQuietly(slot);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScheduledTaskSlot(SemaphoreSlim slot, CancellationTokenSource cts)
        : IScheduledTaskSlot
    {
        public CancellationToken Token => cts.Token;

        public ValueTask DisposeAsync()
        {
            ReleaseQuietly(slot);
            cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
