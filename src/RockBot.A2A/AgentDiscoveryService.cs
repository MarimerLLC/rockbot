using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;

namespace RockBot.A2A;

/// <summary>
/// Hosted service that publishes this agent's <see cref="AgentCard"/> on startup
/// and subscribes to <see cref="A2AOptions.DiscoveryTopic"/> to maintain a local
/// directory of known agents.
/// </summary>
internal sealed class AgentDiscoveryService(
    IMessagePublisher publisher,
    IMessageSubscriber subscriber,
    AgentDirectory directory,
    A2AOptions options,
    Host.AgentIdentity agent,
    ILogger<AgentDiscoveryService> logger) : IHostedService
{
    private ISubscription? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe to discovery announcements
        _subscription = await subscriber.SubscribeAsync(
            options.DiscoveryTopic,
            $"{agent.Name}.discovery",
            HandleDiscoveryMessage,
            cancellationToken);

        logger.LogInformation("Subscribed to discovery topic {Topic}", options.DiscoveryTopic);

        // Announce our own card if configured
        if (options.Card is not null)
        {
            var envelope = options.Card.ToEnvelope<AgentCard>(source: agent.Name);
            await publisher.PublishAsync(options.DiscoveryTopic, envelope, cancellationToken);
            logger.LogInformation("Published agent card for {AgentName}", options.Card.AgentName);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
        }
    }

    private Task<MessageResult> HandleDiscoveryMessage(MessageEnvelope envelope, CancellationToken ct)
    {
        var card = envelope.GetPayload<AgentCard>();
        if (card is null)
        {
            logger.LogWarning("Received invalid agent card on discovery topic");
            return Task.FromResult(MessageResult.DeadLetter);
        }

        directory.AddOrUpdate(card);
        logger.LogDebug("Discovered agent {AgentName}", card.AgentName);
        return Task.FromResult(MessageResult.Ack);
    }
}
