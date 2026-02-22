using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RockBot.ResearchAgent;

/// <summary>
/// Background service that waits for <see cref="EphemeralShutdownCoordinator.NotifyTaskComplete"/>
/// and then triggers graceful host shutdown so the ephemeral pod exits after one task.
/// </summary>
internal sealed class EphemeralShutdownService(
    EphemeralShutdownCoordinator coordinator,
    IHostApplicationLifetime lifetime,
    ILogger<EphemeralShutdownService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await coordinator.WaitForCompletionAsync(stoppingToken);
            logger.LogInformation("Research task complete — stopping ephemeral agent");
            lifetime.StopApplication();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path — host is already stopping
        }
    }
}
