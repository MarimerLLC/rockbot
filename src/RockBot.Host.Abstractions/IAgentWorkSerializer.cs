namespace RockBot.Host;

/// <summary>
/// Serializes full agent loops so that user sessions and scheduled background
/// tasks never run concurrently, and user sessions always preempt background tasks.
/// </summary>
/// <remarks>
/// The agent host holds a single execution slot. At most one full agent loop
/// (user background tool loop or scheduled task loop) occupies the slot at a
/// time. When a user session acquires the slot it first cancels any scheduled
/// task that currently holds it, so the user always gets a fast response.
/// </remarks>
public interface IAgentWorkSerializer
{
    /// <summary>
    /// Acquires the execution slot for a user background tool loop.
    /// Cancels any scheduled task currently holding the slot, then waits for
    /// the slot to become free before returning.
    /// </summary>
    /// <param name="ct">
    /// Cancellation token for the calling session (linked to host lifetime and
    /// to <see cref="SessionBackgroundTaskTracker"/> so a subsequent user
    /// message cancels the waiting loop before it even starts).
    /// </param>
    /// <returns>
    /// A disposable that releases the slot when the loop completes or is cancelled.
    /// </returns>
    Task<IAsyncDisposable> AcquireForUserAsync(CancellationToken ct);

    /// <summary>
    /// Tries to acquire the execution slot for a scheduled background task.
    /// Returns <c>null</c> immediately if the slot is already held (by a user
    /// loop or another task), in which case the caller should skip this run
    /// and wait for the next scheduled tick.
    /// </summary>
    /// <param name="ct">Host lifetime cancellation token.</param>
    /// <returns>
    /// A handle containing a <see cref="CancellationToken"/> linked to both
    /// <paramref name="ct"/> and the preemption signal (fired when a user
    /// message arrives), plus a disposable that releases the slot.
    /// Returns <c>null</c> if the slot could not be acquired.
    /// </returns>
    Task<IScheduledTaskSlot?> TryAcquireForScheduledAsync(CancellationToken ct);
}

/// <summary>
/// Represents a successfully acquired execution slot for a scheduled task.
/// The <see cref="Token"/> fires if a user session preempts the task or the
/// host shuts down. Dispose to release the slot.
/// </summary>
public interface IScheduledTaskSlot : IAsyncDisposable
{
    /// <summary>
    /// Combined cancellation token: fires on host shutdown or user preemption.
    /// Pass this to <c>AgentLoopRunner.RunAsync</c> so the task stops cleanly
    /// when a user message arrives.
    /// </summary>
    CancellationToken Token { get; }
}
