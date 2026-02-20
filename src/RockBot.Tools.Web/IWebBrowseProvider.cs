namespace RockBot.Tools.Web;

/// <summary>
/// Provider-agnostic interface for fetching web page content.
/// </summary>
public interface IWebBrowseProvider
{
    /// <summary>
    /// Fetch a web page and return its content as Markdown.
    /// </summary>
    Task<WebPageContent> FetchAsync(string url, CancellationToken ct);
}
