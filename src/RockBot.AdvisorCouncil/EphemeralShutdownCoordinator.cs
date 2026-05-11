namespace RockBot.AdvisorCouncil;

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

    public void NotifyTaskComplete() => _tcs.TrySetResult(true);

    public Task WaitForCompletionAsync(CancellationToken ct) =>
        _tcs.Task.WaitAsync(ct);
}
