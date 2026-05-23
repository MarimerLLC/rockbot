using RockBot.Messaging;

namespace RockBot.UserProxy.WorkIqAuth.Tests;

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
