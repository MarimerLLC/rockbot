using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;

namespace RockBot.A2A;

/// <summary>
/// Hosted service that publishes this agent's <see cref="AgentCard"/> on startup
/// (and periodically, so late-joining callers can discover it) and subscribes to
/// <see cref="A2AOptions.DiscoveryTopic"/> to maintain a local directory of known agents.
/// </summary>
internal sealed class AgentDiscoveryService(
    IMessagePublisher publisher,
    IMessageSubscriber subscriber,
    AgentDirectory directory,
    AgentCardSummarizer summarizer,
    A2AOptions options,
    Host.AgentIdentity agent,
    ILogger<AgentDiscoveryService> logger) : IHostedService
{
    /// <summary>How often to re-broadcast the agent card so late-joining callers can discover it.</summary>
    private static readonly TimeSpan ReannounceInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Queue retention for the discovery subscription (#650). The queue is durable and
    /// named per identity, so it outlives a stopped agent and keeps collecting every other
    /// agent's re-announcements. An announcement is superseded within one
    /// <see cref="ReannounceInterval"/>, so a backlog is pure waste: messages expire after
    /// a few intervals and are dropped rather than dead-lettered, and a queue unused for a
    /// day is deleted by the broker. Not <see cref="SubscriptionOptions.Ephemeral"/>: that
    /// deletes the queue on dispose, which during a rolling restart would pull it out from
    /// under the replacement pod that shares it. Discovery state is rebuilt from fresh
    /// announcements on start, so nothing is lost when a queue expires.
    /// </summary>
    internal static readonly SubscriptionOptions DiscoverySubscriptionOptions = new()
    {
        MessageTtl = ReannounceInterval * 5,
        IdleExpiry = TimeSpan.FromHours(24),
        DeadLetter = false,
    };

    private ISubscription? _subscription;
    private CancellationTokenSource? _reAnnounceCts;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe to discovery announcements
        _subscription = await subscriber.SubscribeAsync(
            options.DiscoveryTopic,
            $"{agent.Name}.discovery",
            HandleDiscoveryMessage,
            DiscoverySubscriptionOptions,
            cancellationToken);

        logger.LogInformation("Subscribed to discovery topic {Topic}", options.DiscoveryTopic);

        // Kick off summaries for any entries already in the directory (well-known agents
        // and entries restored from persistence) that don't yet have a generated summary.
        _ = Task.Run(async () =>
        {
            foreach (var entry in directory.GetAllEntries().Where(e => e.LlmSummary is null))
            {
                var summary = await summarizer.SummarizeAsync(entry.Card, CancellationToken.None);
                directory.SetSummary(entry.Card.AgentName, summary);
            }
        }, CancellationToken.None);

        // Announce our own card if configured
        if (options.Card is not null)
        {
            await PublishCardAsync(cancellationToken);

            // Periodically re-announce so agents that start after us can discover this agent
            _reAnnounceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = ReAnnounceLoopAsync(_reAnnounceCts.Token);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _reAnnounceCts?.Cancel();
        _reAnnounceCts?.Dispose();

        // Notify other agents that we are going away so they can remove us immediately,
        // rather than waiting for our entry to expire via TTL.
        if (options.Card is not null)
        {
            try
            {
                var deregCard = options.Card with { IsDeregistering = true };
                var envelope = deregCard.ToEnvelope<AgentCard>(source: agent.Name);
                await publisher.PublishAsync(options.DiscoveryTopic, envelope, cancellationToken);
                logger.LogInformation("Published deregistration for agent {AgentName}", options.Card.AgentName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish deregistration for {AgentName}", options.Card.AgentName);
            }
        }

        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }

    private async Task PublishCardAsync(CancellationToken ct)
    {
        var envelope = options.Card!.ToEnvelope<AgentCard>(source: agent.Name);
        await publisher.PublishAsync(options.DiscoveryTopic, envelope, ct);
        logger.LogInformation("Published agent card for {AgentName}", options.Card!.AgentName);
    }

    private async Task ReAnnounceLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ReannounceInterval, ct);
                await PublishCardAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Re-announce loop for {AgentName} failed", options.Card?.AgentName);
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

        if (card.IsDeregistering)
        {
            directory.Remove(card.AgentName);
        }
        else
        {
            var isNew = directory.GetAgent(card.AgentName) is null;
            directory.AddOrUpdate(card);
            logger.LogDebug("Discovered agent {AgentName}", card.AgentName);

            // Generate a summary for new agents (fire-and-forget — cosmetic, should not block).
            // Re-announcements from known agents keep their existing summary.
            if (isNew)
            {
                _ = Task.Run(async () =>
                {
                    var summary = await summarizer.SummarizeAsync(card, CancellationToken.None);
                    directory.SetSummary(card.AgentName, summary);
                }, CancellationToken.None);
            }
        }

        return Task.FromResult(MessageResult.Ack);
    }
}
