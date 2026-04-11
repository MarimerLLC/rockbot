namespace RockBot.Host;

/// <summary>
/// Queue for inbound A2A notifications that accumulate while the user is busy.
/// Notifications are drained and presented as a batch when the user becomes idle.
/// </summary>
public interface IInboundNotificationQueue
{
    /// <summary>Adds a notification to the queue.</summary>
    Task EnqueueAsync(InboundNotification notification, CancellationToken ct);

    /// <summary>Removes and returns all queued notifications.</summary>
    Task<IReadOnlyList<InboundNotification>> DrainAsync(CancellationToken ct);

    /// <summary>Number of notifications waiting to be presented.</summary>
    int PendingCount { get; }
}
