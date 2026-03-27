namespace RockBot.UserProxy;

/// <summary>
/// Response containing all saved response summaries.
/// </summary>
public sealed record ListSavedResponsesResponse
{
    public required IReadOnlyList<SavedResponseSummary> Items { get; init; }
}
