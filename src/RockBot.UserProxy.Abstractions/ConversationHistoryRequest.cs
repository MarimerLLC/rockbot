namespace RockBot.UserProxy;

/// <summary>
/// Requests the full conversation history for a session.
/// </summary>
public sealed record ConversationHistoryRequest
{
    public required string SessionId { get; init; }
}
