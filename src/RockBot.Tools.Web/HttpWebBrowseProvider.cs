using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Web;

/// <summary>
/// Fetches a page with a plain HTTP GET and converts it to Markdown. No JavaScript runs,
/// so consent walls and client-rendered pages come back as shells; the result's
/// <see cref="WebPageContent.Status"/> says when that happened.
/// </summary>
internal sealed class HttpWebBrowseProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpWebBrowseProvider> logger,
    WebToolOptions? options = null) : IWebBrowseProvider
{
    private readonly WebToolOptions _options = options ?? new WebToolOptions();

    public async Task<WebPageContent> FetchAsync(string url, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("RockBot.Tools.Web.Browse");
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);

        var page = await HtmlPageConverter.ConvertAsync(html, _options.ShellMinContentChars, ct);

        logger.LogDebug("Fetched and converted {Url} ({Length} chars of Markdown, status {Status})",
            url, page.Markdown.Length, page.Status);

        return new WebPageContent
        {
            Title = page.Title,
            Content = page.Markdown,
            SourceUrl = url,
            Status = page.Status,
            FetchedVia = WebFetchMethod.Http
        };
    }
}
