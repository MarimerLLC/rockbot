using AngleSharp;
using AngleSharp.Dom;

namespace RockBot.Tools.Web;

/// <summary>A page converted to Markdown, with a verdict on whether it held real content.</summary>
internal sealed record ConvertedPage(string Title, string Markdown, WebFetchStatus Status);

/// <summary>
/// Turns raw HTML into Markdown for <c>web_browse</c>. Shared by the plain HTTP path and
/// the headless-browser path so both produce the same shape of output.
/// </summary>
internal static class HtmlPageConverter
{
    // Elements that add noise without useful content. [hidden] covers banners and dialogs
    // the page has not shown yet — a hidden cookie banner was once all that survived.
    internal const string NoiseSelector =
        "script, style, noscript, template, svg, iframe, nav, header, footer, aside, " +
        "figure, picture, form, [hidden], [role=navigation], [role=banner], [role=contentinfo]";

    public static async Task<ConvertedPage> ConvertAsync(string html, int shellMinContentChars, CancellationToken ct)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html), ct);

        var title = document.Title ?? string.Empty;

        // Consent banners come out before measuring, so a wall of cookie legalese is not
        // mistaken for page content.
        var consentElements = document.QuerySelectorAll(PageShellDetector.ConsentSelector).ToList();
        foreach (var element in consentElements)
            element.Remove();

        foreach (var element in document.QuerySelectorAll(NoiseSelector).ToList())
            element.Remove();

        var cleanedHtml = document.Body?.InnerHtml ?? html;

        var converterConfig = new ReverseMarkdown.Config
        {
            // Bypass, not Drop: Drop discards an unrecognised element *with its children*, and
            // ReverseMarkdown does not recognise <main>, <article> or <section> — so Drop threw
            // away the body of any page that wraps its content in them.
            UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Bypass,
            GithubFlavored = true,
            RemoveComments = true,
        };
        var markdown = new ReverseMarkdown.Converter(converterConfig).Convert(cleanedHtml);

        var status = PageShellDetector.Classify(html, markdown, consentElements.Count > 0, shellMinContentChars);
        return new ConvertedPage(title, markdown, status);
    }
}
