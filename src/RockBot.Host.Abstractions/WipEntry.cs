namespace RockBot.Host;

/// <summary>
/// A single work-in-progress entry representing a message that has been received
/// from the bus and is currently being processed. Persisted to disk so that
/// incomplete work can be recovered after a pod restart.
/// </summary>
public sealed record WipEntry(
    string MessageId,
    string MessageType,
    string? CorrelationId,
    string? ReplyTo,
    string Source,
    string? Destination,
    DateTimeOffset MessageTimestamp,
    DateTimeOffset StartedAt,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body);
