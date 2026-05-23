using RockBot.Messaging;

namespace RockBot.Agent.Tests.McpBridge.Auth;

/// <summary>
/// Test stub for <see cref="IMessageSubscriber"/>. Captures the registered
/// handler so tests can drive it directly, simulating bus delivery without
/// spinning up RabbitMQ.
/// </summary>
internal sealed class StubMessageSubscriber : IMessageSubscriber
{
    public Func<MessageEnvelope, CancellationToken, Task<MessageResult>>? Handler { get; private set; }
    public string? Topic { get; private set; }
    public string? SubscriptionName { get; private set; }

    public Task<ISubscription> SubscribeAsync(
        string topic,
        string subscriptionName,
        Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
        CancellationToken cancellationToken = default,
        int dispatchConcurrency = 1)
    {
        Topic = topic;
        SubscriptionName = subscriptionName;
        Handler = handler;
        return Task.FromResult<ISubscription>(new StubSubscription());
    }

    public ValueTask DisposeAsync() => default;

    private sealed class StubSubscription : ISubscription
    {
        public string Topic => string.Empty;
        public string SubscriptionName => string.Empty;
        public bool IsActive => true;
        public ValueTask DisposeAsync() => default;
    }
}
