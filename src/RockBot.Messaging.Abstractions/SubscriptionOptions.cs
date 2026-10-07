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
    /// Maximum number of messages processed concurrently. Default 1 (sequential).
    /// See <see cref="IMessageSubscriber.SubscribeAsync(string, string, Func{MessageEnvelope, CancellationToken, Task{MessageResult}}, CancellationToken, int)"/>.
    /// </summary>
    public int DispatchConcurrency { get; init; } = 1;
}
