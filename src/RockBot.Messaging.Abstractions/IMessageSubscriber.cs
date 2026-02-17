namespace RockBot.Messaging;

/// <summary>
/// Subscribes to messages on a topic. Each subscription gets its own
/// consumer group (queue) so multiple subscribers can independently
/// process the same topic.
/// </summary>
public interface IMessageSubscriber : IAsyncDisposable
{
    /// <summary>
    /// Subscribe to a topic with a handler callback.
    /// </summary>
    /// <param name="topic">Topic pattern to subscribe to. Supports wildcards
    /// (e.g. "agent.*", "agent.#" depending on the provider).</param>
    /// <param name="subscriptionName">Logical name for this subscription,
    /// used to create a durable consumer group/queue.</param>
    /// <param name="handler">Async handler invoked for each message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A subscription handle that can be disposed to unsubscribe.</returns>
    Task<ISubscription> SubscribeAsync(
        string topic,
        string subscriptionName,
        Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
        CancellationToken cancellationToken = default);
}
