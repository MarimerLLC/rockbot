namespace RockBot.Host;

/// <summary>
/// A single turn in a conversation session.
/// </summary>
/// <param name="Role">The role of the participant (e.g. "user", "assistant").</param>
/// <param name="Content">The content of the turn.</param>
/// <param name="Timestamp">When the turn occurred.</param>
public sealed record ConversationTurn(string Role, string Content, DateTimeOffset Timestamp);
