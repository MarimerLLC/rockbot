using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Web;

internal sealed class HttpWebBrowseProvider(
    IHttpClientFactory httpClientFactory,
    WebToolOptions options,
    ILogger<HttpWebBrowseProvider> logger) : IWebBrowseProvider
{
    // Elements that add noise without useful content
    private const string NoiseSelector =
        "script, style, noscript, svg, iframe, nav, header, footer, aside, " +
        "figure, picture, form, [role=navigation], [role=banner], [role=contentinfo]";

    public async Task<WebPageContent> FetchAsync(string url, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("RockBot.Tools.Web.Browse");
        var html = await client.GetStringAsync(url, ct);

        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = await context.OpenAsync(req => req.Content(html), ct);

        var title = document.Title ?? string.Empty;

        foreach (var element in document.QuerySelectorAll(NoiseSelector).ToList())
            element.Remove();

        var cleanedHtml = document.Body?.InnerHtml ?? html;

        var converter = new ReverseMarkdown.Converter();
        var markdown = converter.Convert(cleanedHtml);

        var maxLen = options.MaxBrowseContentLength;
        if (markdown.Length > maxLen)
        {
            markdown = markdown[..maxLen] +
                $"\n\n[Content truncated — {markdown.Length - maxLen:N0} chars omitted]";
        }

        logger.LogDebug("Fetched and converted {Url} ({Length} chars of Markdown)", url, markdown.Length);

        return new WebPageContent
        {
            Title = title,
            Content = markdown,
            SourceUrl = url
        };
    }
}
