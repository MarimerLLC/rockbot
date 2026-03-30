namespace RockBot.UserProxy;

/// <summary>
/// Message sent from the user proxy to cancel all in-flight work for a session.
/// </summary>
public sealed record CancelSessionRequest
{
    public required string SessionId { get; init; }
}
