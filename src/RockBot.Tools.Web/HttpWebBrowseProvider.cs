using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Web;

internal sealed class HttpWebBrowseProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpWebBrowseProvider> logger) : IWebBrowseProvider
{
    public async Task<WebPageContent> FetchAsync(string url, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("RockBot.Tools.Web.Browse");
        var html = await client.GetStringAsync(url, ct);

        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = await context.OpenAsync(req => req.Content(html), ct);

        var title = document.Title ?? string.Empty;

        foreach (var element in document.QuerySelectorAll("script, style").ToList())
            element.Remove();

        var cleanedHtml = document.Body?.InnerHtml ?? html;

        var converter = new ReverseMarkdown.Converter();
        var markdown = converter.Convert(cleanedHtml);

        logger.LogDebug("Fetched and converted {Url} ({Length} chars of Markdown)", url, markdown.Length);

        return new WebPageContent
        {
            Title = title,
            Content = markdown,
            SourceUrl = url
        };
    }
}
