using RockBot.Messaging;

namespace RockBot.Messaging.InProcess;

internal sealed class InProcessPublisher : IMessagePublisher
{
    private readonly InProcessBus _bus;

    public InProcessPublisher(InProcessBus bus)
    {
        _bus = bus;
    }

    public async Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        await _bus.DeliverAsync(topic, envelope, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
