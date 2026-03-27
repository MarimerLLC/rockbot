namespace RockBot.UserProxy;

/// <summary>
/// Request to retrieve a single saved response by ID.
/// </summary>
public sealed record GetSavedResponseRequest
{
    public required string Id { get; init; }
}
