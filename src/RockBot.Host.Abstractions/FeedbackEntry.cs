namespace RockBot.Host;

/// <summary>
/// A single recorded quality signal from an agent session.
/// </summary>
public sealed record FeedbackEntry(
    string Id,
    string SessionId,
    FeedbackSignalType SignalType,
    string Summary,
    string? Detail,
    DateTimeOffset Timestamp);
