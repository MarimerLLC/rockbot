namespace RockBot.UserProxy;

/// <summary>
/// Request to delete a saved response by ID.
/// </summary>
public sealed record DeleteSavedResponseRequest
{
    public required string Id { get; init; }
}
