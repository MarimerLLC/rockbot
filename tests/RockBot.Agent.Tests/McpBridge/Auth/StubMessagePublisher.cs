using RockBot.Messaging;

namespace RockBot.Agent.Tests.McpBridge.Auth;

/// <summary>
/// Test stub for <see cref="IMessagePublisher"/>. Records all published
/// envelopes so tests can assert on them.
/// </summary>
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
