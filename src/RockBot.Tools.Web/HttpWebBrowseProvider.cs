using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Web;

internal sealed class HttpWebBrowseProvider(
    IHttpClientFactory httpClientFactory,
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

        var converterConfig = new ReverseMarkdown.Config
        {
            // Drop unrecognised tags (div, section, main, etc.) instead of passing
            // their raw HTML through — keeps the output clean Markdown.
            UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Drop,
            GithubFlavored = true,
            RemoveComments = true,
        };
        var converter = new ReverseMarkdown.Converter(converterConfig);
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
