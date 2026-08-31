namespace RockBot.Messaging;

/// <summary>
/// Handle to an active subscription. Dispose to unsubscribe.
/// </summary>
public interface ISubscription : IAsyncDisposable
{
    /// <summary>
    /// The topic this subscription is listening on.
    /// </summary>
    string Topic { get; }

    /// <summary>
    /// The logical subscription/consumer group name.
    /// </summary>
    string SubscriptionName { get; }

    /// <summary>
    /// Whether this subscription is currently active.
    /// </summary>
    bool IsActive { get; }
}
