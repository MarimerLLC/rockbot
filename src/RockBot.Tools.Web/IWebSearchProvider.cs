namespace RockBot.Tools.Web;

/// <summary>
/// Provider-agnostic interface for web search.
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>
    /// Search the web and return a list of results.
    /// </summary>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct);
}
