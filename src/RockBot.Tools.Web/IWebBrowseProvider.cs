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

    /// <summary>
    /// Fetch a web page with per-call options. Providers that cannot honour the options
    /// fall back to <see cref="FetchAsync(string, CancellationToken)"/>.
    /// </summary>
    Task<WebPageContent> FetchAsync(string url, WebFetchOptions options, CancellationToken ct)
        => FetchAsync(url, ct);
}

/// <summary>
/// Per-call options for <see cref="IWebBrowseProvider.FetchAsync(string, WebFetchOptions, CancellationToken)"/>.
/// </summary>
public sealed record WebFetchOptions
{
    public static readonly WebFetchOptions Default = new();

    /// <summary>
    /// Render the page in the headless browser first instead of trying a plain HTTP GET.
    /// Ignored when no render service is configured.
    /// </summary>
    public bool ForceRender { get; init; }
}
