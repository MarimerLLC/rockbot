namespace RockBot.UserProxy;

/// <summary>
/// Acknowledgment for a delete-saved-response request.
/// </summary>
public sealed record DeleteSavedResponseAck
{
    public required bool Success { get; init; }
}
