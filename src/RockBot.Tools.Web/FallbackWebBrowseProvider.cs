using System.Net;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Web;

/// <summary>
/// Plain HTTP first, the headless browser when that is not enough. A page goes to the
/// browser when the HTTP result is a shell (consent wall, JavaScript loader, empty body),
/// when the site refuses the HTTP client outright (403/503), or when the caller asks for
/// rendering. Without a render service the HTTP result comes back with its shell status,
/// so the model still learns that it did not get the page.
/// </summary>
internal sealed class FallbackWebBrowseProvider(
    HttpWebBrowseProvider http,
    RenderServiceWebBrowseProvider render,
    ILogger<FallbackWebBrowseProvider> logger) : IWebBrowseProvider
{
    internal const string RenderUnavailable = "Headless rendering is not available in this deployment.";
    internal const string RenderStillEmpty = "Headless rendering was tried but the page still showed no content.";

    public Task<WebPageContent> FetchAsync(string url, CancellationToken ct)
        => FetchAsync(url, WebFetchOptions.Default, ct);

    public async Task<WebPageContent> FetchAsync(string url, WebFetchOptions options, CancellationToken ct)
    {
        WebPageContent? forced = null;
        string? forcedFailure = null;

        if (options.ForceRender && render.IsEnabled)
        {
            (forced, forcedFailure) = await TryRenderAsync(url, ct);
            if (forced is { Status: WebFetchStatus.Ok })
                return forced;
            forcedFailure ??= RenderStillEmpty;
        }

        WebPageContent page;
        try
        {
            page = await http.FetchAsync(url, ct);
        }
        catch (HttpRequestException ex) when (IsRefusal(ex) && render.IsEnabled && forcedFailure is null)
        {
            logger.LogInformation("web_browse: {Url} refused the HTTP client ({Status}); rendering", url, ex.StatusCode);
            var (rendered, _) = await TryRenderAsync(url, ct);
            if (rendered is null)
                throw;
            return rendered;
        }

        if (page.Status == WebFetchStatus.Ok)
            return options.ForceRender ? page with { Note = forcedFailure ?? RenderUnavailable } : page;

        if (!render.IsEnabled)
            return page with { Note = RenderUnavailable };

        // A forced render already ran and did not help; don't render twice.
        if (forcedFailure is not null)
            return Better(forced, page) with { Note = forcedFailure };

        logger.LogInformation("web_browse: HTTP result for {Url} is a shell ({Status}); rendering", url, page.Status);
        var (second, failure) = await TryRenderAsync(url, ct);
        if (second is null)
            return page with { Note = failure };
        if (second.Status == WebFetchStatus.Ok)
            return second;

        return Better(second, page) with { Note = RenderStillEmpty };
    }

    // Both came back as shells — keep whichever said more.
    private static WebPageContent Better(WebPageContent? rendered, WebPageContent http) =>
        rendered is not null &&
        PageShellDetector.ReadableLength(rendered.Content) >= PageShellDetector.ReadableLength(http.Content)
            ? rendered
            : http;

    private async Task<(WebPageContent? Page, string? Failure)> TryRenderAsync(string url, CancellationToken ct)
    {
        try
        {
            return (await render.FetchAsync(url, ct), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("web_browse: rendering {Url} failed: {Message}", url, ex.Message);
            return (null, $"Headless rendering was tried and failed: {ex.Message}");
        }
    }

    private static bool IsRefusal(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable;
}
