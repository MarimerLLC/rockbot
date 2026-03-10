namespace RockBot.Messaging;

/// <summary>
/// Provides read access to dead-letter queues for inspection and remediation.
/// Implementations query the message broker's management API.
/// </summary>
public interface IDlqSampler
{
    /// <summary>
    /// Returns all dead-letter queues with their current message counts.
    /// An empty list is returned when the management API is unavailable or not configured.
    /// </summary>
    Task<IReadOnlyList<DlqQueueInfo>> GetDlqQueuesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a sample of up to <paramref name="maxCount"/> messages from the specified DLQ.
    /// Messages are re-queued after reading (non-destructive peek).
    /// </summary>
    Task<IReadOnlyList<DlqMessage>> SampleMessagesAsync(
        string queueName,
        int maxCount,
        CancellationToken ct = default);

    /// <summary>Deletes all messages from the specified dead-letter queue.</summary>
    Task PurgeQueueAsync(string queueName, CancellationToken ct = default);
}

/// <summary>A dead-letter queue and its current message count.</summary>
public sealed record DlqQueueInfo(string Name, long MessageCount);

/// <summary>A message sampled from a dead-letter queue.</summary>
public sealed record DlqMessage(
    string? MessageId,
    string? MessageType,
    string? Source,
    string? Destination,
    string? RoutingKey,
    string? DeathReason,
    int DeathCount,
    DateTimeOffset? DeadLetteredAt,
    string BodyPreview);
