using Microsoft.Extensions.Logging;
using RockBot.Messaging;

namespace RockBot.Messaging.InProcess;

internal sealed class InProcessSubscriber : IMessageSubscriber
{
    private readonly InProcessBus _bus;
    private readonly ILogger<InProcessSubscriber> _logger;

    public InProcessSubscriber(InProcessBus bus, ILogger<InProcessSubscriber> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    public Task<ISubscription> SubscribeAsync(
        string topic,
        string subscriptionName,
        Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
        CancellationToken cancellationToken = default)
    {
        var subscription = new InProcessSubscription(topic, subscriptionName, handler, _bus, _logger);
        _bus.Register(subscription);
        return Task.FromResult<ISubscription>(subscription);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
