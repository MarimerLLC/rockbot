namespace RockBot.Messaging;

/// <summary>
/// Envelope wrapping every message flowing through the system.
/// Carries routing and correlation metadata independent of the payload.
/// </summary>
public sealed record MessageEnvelope
{
    public required string MessageId { get; init; }
    public required string MessageType { get; init; }
    public string? CorrelationId { get; init; }
    public string? ReplyTo { get; init; }
    public required string Source { get; init; }
    public string? Destination { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required ReadOnlyMemory<byte> Body { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Creates a new envelope with standard defaults.
    /// </summary>
    public static MessageEnvelope Create(
        string messageType,
        ReadOnlyMemory<byte> body,
        string source,
        string? correlationId = null,
        string? replyTo = null,
        string? destination = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        return new MessageEnvelope
        {
            MessageId = Guid.NewGuid().ToString("N"),
            MessageType = messageType,
            Body = body,
            Source = source,
            CorrelationId = correlationId,
            ReplyTo = replyTo,
            Destination = destination,
            Timestamp = DateTimeOffset.UtcNow,
            Headers = headers ?? new Dictionary<string, string>()
        };
    }
}
