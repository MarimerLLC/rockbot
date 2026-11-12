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
internal sealed class ActiveWorkStartupAnnouncer(
    IAgentEventPublisher eventPublisher,
    ILogger<ActiveWorkStartupAnnouncer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
