namespace RockBot.ResearchAgent;

/// <summary>
/// Signals that the single task handled by this ephemeral pod is complete.
/// The <see cref="EphemeralShutdownService"/> waits on this coordinator and calls
/// <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.StopApplication"/>
/// once notified, allowing the pod to exit cleanly after one task.
/// </summary>
internal sealed class EphemeralShutdownCoordinator
{
    private readonly TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Signals that the task is complete and the pod should shut down.
    /// Safe to call multiple times — only the first call has effect.
    /// </summary>
    public void NotifyTaskComplete() => _tcs.TrySetResult(true);

    /// <summary>
    /// Returns a task that completes when <see cref="NotifyTaskComplete"/> is called,
    /// or when <paramref name="ct"/> is cancelled.
    /// </summary>
    public Task WaitForCompletionAsync(CancellationToken ct) =>
        _tcs.Task.WaitAsync(ct);
}
