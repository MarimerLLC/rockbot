namespace RockBot.Host;

/// <summary>
/// A single logged turn in a conversation, used by the preference-inference dream pass.
/// </summary>
/// <param name="SessionId">The session this turn belongs to.</param>
/// <param name="Role">The role of the participant (e.g. "user", "assistant").</param>
/// <param name="Content">The content of the turn.</param>
/// <param name="Timestamp">When the turn occurred.</param>
public sealed record ConversationLogEntry(
    string SessionId,
    string Role,
    string Content,
    DateTimeOffset Timestamp);
