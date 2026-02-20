namespace RockBot.UserProxy;

/// <summary>
/// Returns the full conversation history for a session.
/// </summary>
public sealed record ConversationHistoryResponse
{
    public required IReadOnlyList<ConversationHistoryTurn> Turns { get; init; }
}
