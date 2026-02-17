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
    private readonly AgentIdentity _identity;
    private readonly AgentHostOptions _options;
    private readonly ILogger<AgentHost> _logger;
    private readonly List<ISubscription> _subscriptions = [];

    public AgentHost(
        IMessageSubscriber subscriber,
        IMessagePipeline pipeline,
        AgentIdentity identity,
        IOptions<AgentHostOptions> options,
        ILogger<AgentHost> logger)
    {
        _subscriber = subscriber;
        _pipeline = pipeline;
        _identity = identity;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting agent {AgentName} ({InstanceId})",
            _identity.Name, _identity.InstanceId);

        foreach (var topic in _options.Topics)
        {
            var sanitizedTopic = topic.Replace(".", "-").Replace("*", "_").Replace("#", "__");
            var subscriptionName = $"{_identity.Name}.{sanitizedTopic}";

            var subscription = await _subscriber.SubscribeAsync(
                topic,
                subscriptionName,
                (envelope, ct) => _pipeline.DispatchAsync(envelope, ct),
                cancellationToken);

            _subscriptions.Add(subscription);
            _logger.LogInformation("Subscribed to {Topic} as {SubscriptionName}", topic, subscriptionName);
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
}
