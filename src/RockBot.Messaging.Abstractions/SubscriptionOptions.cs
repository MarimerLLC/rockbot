namespace RockBot.Messaging;

/// <summary>
/// Per-subscription options for <see cref="IMessageSubscriber"/>.
/// </summary>
public sealed record SubscriptionOptions
{
    /// <summary>
    /// The subscription belongs to a single process lifetime (a CLI run, a per-request
    /// reply queue, a per-pod listener). Providers that create durable queues delete
    /// them when the subscription is disposed and let the broker expire them after an
    /// idle period, so a crashed or killed process does not leave queues behind.
    /// Default false: the subscription is a durable, shared consumer group.
    /// </summary>
    public bool Ephemeral { get; init; }

    /// <summary>
    /// How long a message may wait in the subscription's queue before the broker discards
    /// it. Use for latest-state fan-out (discovery announcements, heartbeats), where a
    /// message is worthless once a newer one has superseded it. Overrides the provider's
    /// ephemeral default when <see cref="Ephemeral"/> is also set. Null: no TTL (or the
    /// provider's ephemeral default).
    /// </summary>
    public TimeSpan? MessageTtl { get; init; }

    /// <summary>
    /// How long the subscription's queue may go unused (no consumer) before the broker
    /// deletes it. Use for a durable queue whose owner can stop for good, so a retired
    /// identity's queue does not keep collecting fan-out traffic forever. Unlike
    /// <see cref="Ephemeral"/>, the queue is not deleted on dispose, so replicas and
    /// rolling restarts that share it are unaffected. Overrides the provider's ephemeral
    /// default when <see cref="Ephemeral"/> is also set. Null: no idle expiry (or the
    /// provider's ephemeral default).
    /// </summary>
    public TimeSpan? IdleExpiry { get; init; }

    /// <summary>
    /// Whether rejected and expired messages are routed to a dead-letter queue. Set false
    /// when a dead-letter would carry no diagnostic value, e.g. with a short
    /// <see cref="MessageTtl"/>, where every expiry would otherwise be dead-lettered.
    /// Such messages are then dropped. Default true.
    /// </summary>
    public bool DeadLetter { get; init; } = true;

    /// <summary>
    /// Maximum number of messages processed concurrently. Default 1 (sequential).
    /// See <see cref="IMessageSubscriber.SubscribeAsync(string, string, Func{MessageEnvelope, CancellationToken, Task{MessageResult}}, CancellationToken, int)"/>.
    /// </summary>
    public int DispatchConcurrency { get; init; } = 1;
}
