using RockBot.Messaging;

namespace RockBot.UserProxy.WorkIqAuth.Tests;

internal sealed class StubMessagePublisher : IMessagePublisher
{
    public List<(string Topic, MessageEnvelope Envelope)> Published { get; } = new();

    public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        Published.Add((topic, envelope));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => default;
}
