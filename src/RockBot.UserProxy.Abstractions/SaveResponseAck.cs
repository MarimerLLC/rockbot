namespace RockBot.UserProxy;

/// <summary>
/// Acknowledgment for a save-response request.
/// </summary>
public sealed record SaveResponseAck
{
    public required string Id { get; init; }
    public required bool Success { get; init; }
}
