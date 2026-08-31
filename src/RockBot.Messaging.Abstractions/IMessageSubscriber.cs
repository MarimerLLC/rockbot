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
    /// <param name="dispatchConcurrency">Maximum number of messages from this
    /// subscription that may be processed concurrently. Default 1 (sequential
    /// processing — preserves message ordering). Bump higher when the handler
    /// is re-entrant and may block on cross-message coordination (e.g. a
    /// consolidation gate that waits for siblings). Providers that don't
    /// support per-subscription concurrency may ignore this hint. Placed after
    /// <paramref name="cancellationToken"/> so positional callers that pass
    /// only a CT keep working unchanged.</param>
    /// <returns>A subscription handle that can be disposed to unsubscribe.</returns>
    Task<ISubscription> SubscribeAsync(
        string topic,
        string subscriptionName,
        Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
        CancellationToken cancellationToken = default,
        int dispatchConcurrency = 1);
}
