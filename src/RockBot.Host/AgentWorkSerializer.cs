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
        // Non-blocking: if the slot is held by a user loop, skip this run.
        if (!_slot.Wait(0))
            return Task.FromResult<IScheduledTaskSlot?>(null);

        CancellationToken preemptToken;
        lock (_preemptLock)
        {
            preemptToken = _preemptCts.Token;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, preemptToken);
        return Task.FromResult<IScheduledTaskSlot?>(new ScheduledTaskSlot(_slot, linked));
    }

    public void Dispose()
    {
        lock (_preemptLock)
        {
            _preemptCts.Dispose();
        }
        _slot.Dispose();
    }

    // ── Handles ───────────────────────────────────────────────────────────────

    private sealed class SlotHandle(SemaphoreSlim slot) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            slot.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScheduledTaskSlot(SemaphoreSlim slot, CancellationTokenSource cts)
        : IScheduledTaskSlot
    {
        public CancellationToken Token => cts.Token;

        public ValueTask DisposeAsync()
        {
            slot.Release();
            cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
