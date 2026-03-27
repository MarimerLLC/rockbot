namespace RockBot.UserProxy;

/// <summary>
/// Response containing the full content of a saved response.
/// </summary>
public sealed record GetSavedResponseResponse
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Content { get; init; }
    public required string AgentName { get; init; }
    public required DateTimeOffset SavedAt { get; init; }
    public bool Found { get; init; } = true;
}
