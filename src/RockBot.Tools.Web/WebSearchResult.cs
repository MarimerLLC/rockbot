namespace RockBot.Tools.Web;

/// <summary>
/// A single web search result.
/// </summary>
public sealed record WebSearchResult
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string Snippet { get; init; }
}
