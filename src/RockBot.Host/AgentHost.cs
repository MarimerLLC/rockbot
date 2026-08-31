using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Messaging;

namespace RockBot.Host;

/// <summary>
/// Hosted service that subscribes to configured topics and dispatches
/// messages through the pipeline.
/// </summary>
internal sealed class AgentHost : IHostedService
{
    private readonly IMessageSubscriber _subscriber;
    private readonly IMessagePipeline _pipeline;
    private readonly IWipTracker _wipTracker;
    private readonly AgentIdentity _identity;
    private readonly AgentHostOptions _options;
    private readonly WipOptions _wipOptions;
    private readonly ILogger<AgentHost> _logger;
    private readonly List<ISubscription> _subscriptions = [];

    public AgentHost(
        IMessageSubscriber subscriber,
        IMessagePipeline pipeline,
        IWipTracker wipTracker,
        AgentIdentity identity,
        IOptions<AgentHostOptions> options,
        IOptions<WipOptions> wipOptions,
        ILogger<AgentHost> logger)
    {
        _subscriber = subscriber;
        _pipeline = pipeline;
        _wipTracker = wipTracker;
        _identity = identity;
        _options = options.Value;
        _wipOptions = wipOptions.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting agent {AgentName} ({InstanceId})",
            _identity.Name, _identity.InstanceId);

        await RecoverWipEntriesAsync(cancellationToken);

        foreach (var sub in _options.Topics)
        {
            var sanitizedTopic = sub.Topic.Replace(".", "-").Replace("*", "_").Replace("#", "__");
            var subscriptionName = $"{_identity.Name}.{sanitizedTopic}";

            var subscription = await _subscriber.SubscribeAsync(
                sub.Topic,
                subscriptionName,
                (envelope, ct) => _pipeline.DispatchAsync(envelope, ct),
                cancellationToken,
                sub.DispatchConcurrency);

            _subscriptions.Add(subscription);
            _logger.LogInformation(
                "Subscribed to {Topic} as {SubscriptionName} (dispatchConcurrency={Concurrency})",
                sub.Topic, subscriptionName, sub.DispatchConcurrency);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping agent {AgentName}", _identity.Name);

        foreach (var subscription in _subscriptions)
        {
            await subscription.DisposeAsync();
        }
        _subscriptions.Clear();
    }

    private async Task RecoverWipEntriesAsync(CancellationToken cancellationToken)
    {
        var incomplete = await _wipTracker.GetIncompleteAsync(cancellationToken);
        if (incomplete.Count == 0) return;

        _logger.LogInformation("Found {Count} incomplete WIP entry(ies) — recovering", incomplete.Count);
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in incomplete)
        {
            var age = now - entry.StartedAt;

            if (age > _wipOptions.StaleThreshold)
            {
                _logger.LogWarning(
                    "Abandoning stale WIP entry {MessageId} type={MessageType} (age: {Age})",
                    entry.MessageId, entry.MessageType, age);
                await _wipTracker.AbandonAsync(entry.MessageId, $"Stale: {age}", cancellationToken);
                continue;
            }

            _logger.LogInformation(
                "Recovering WIP entry {MessageId} type={MessageType} (age: {Age})",
                entry.MessageId, entry.MessageType, age);

            // Complete the old entry before re-dispatch so WipMiddleware creates
            // a fresh one — prevents double-entry if recovery itself crashes.
            await _wipTracker.CompleteAsync(entry.MessageId, cancellationToken);

            // Reconstruct the envelope with a recovery header so handlers can
            // detect replay and adjust behavior (e.g. skip duplicate progress messages).
            var headers = new Dictionary<string, string>(entry.Headers)
            {
                [WipConstants.RecoveryHeader] = "true"
            };

            var envelope = new MessageEnvelope
            {
                MessageId = entry.MessageId,
                MessageType = entry.MessageType,
                CorrelationId = entry.CorrelationId,
                ReplyTo = entry.ReplyTo,
                Source = entry.Source,
                Destination = entry.Destination,
                Timestamp = entry.MessageTimestamp,
                Body = entry.Body,
                Headers = headers
            };

            try
            {
                await _pipeline.DispatchAsync(envelope, cancellationToken);
                HostDiagnostics.WipRecovered.Add(1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover WIP entry {MessageId}", entry.MessageId);
            }
        }
    }
}
