using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Broadcasts an empty <see cref="ActiveStatusResponse"/> on <see cref="UserProxyTopics.UserEvent"/>
/// when the agent starts (#795). Nothing survives a restart, so a user interface still showing
/// subagents or workers from before it clears them.
/// </summary>
/// <remarks>
/// Sent from <see cref="IHostedLifecycleService.StartingAsync"/>, which the host runs for every
/// lifecycle service before any hosted service's <c>StartAsync</c>, so the reset goes out before
/// <c>AgentHost</c> starts consuming messages and can't clear work started after the restart.
/// </remarks>
internal sealed class ActiveWorkStartupAnnouncer(
    IAgentEventPublisher eventPublisher,
    ILogger<ActiveWorkStartupAnnouncer> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await eventPublisher.BroadcastAsync(
                new ActiveStatusResponse { Subagents = [], IsProcessing = false, Work = [] },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not announce the empty active-work snapshot");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
