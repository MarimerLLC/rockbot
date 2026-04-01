namespace RockBot.UserProxy;

/// <summary>
/// Message sent from the user proxy to clear conversation context for a session.
/// Long-term memory and conversation logs are preserved.
/// </summary>
public sealed record ClearContextRequest
{
    public required string SessionId { get; init; }
}
