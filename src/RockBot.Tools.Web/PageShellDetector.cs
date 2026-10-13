using System.Text.RegularExpressions;

namespace RockBot.Tools.Web;

/// <summary>
/// Decides whether a fetched page is real content or a shell standing in for it — a
/// cookie-consent wall, a JavaScript loader, or an empty body. A shell must never reach
/// the model looking like a normal page: it reads as "the page says nothing", and the
/// model fills the gap from search snippets.
/// </summary>
internal static partial class PageShellDetector
{
    /// <summary>
    /// Containers of the common consent platforms, plus the plain ids and classes sites use
    /// for home-grown banners. Exact ids and class tokens only — a substring match on
    /// "cookie" would also hit a recipe site.
    /// </summary>
    internal const string ConsentSelector =
        "#onetrust-consent-sdk, #onetrust-banner-sdk, #CybotCookiebotDialog, #didomi-host, " +
        "#usercentrics-root, #qc-cmp2-container, .qc-cmp2-container, [id^=sp_message_container], " +
        "#cmpbox, #cookie-banner, #cookieBanner, #cookie-consent, #cookieConsent, #cookie-notice, " +
        "#cookie-law-info-bar, .cc-window, .cookie-banner, .cookie-consent, .cookie-notice";

    // Readable text this short, with no other clue, means the page came back empty.
    private const int EmptyChars = 50;

    private static readonly string[] ConsentMarkers =
    [
        "onetrust", "cookiebot", "didomi", "usercentrics", "quantcast", "consentmanager",
        "cookieconsent", "cookie-consent", "cookie consent", "we use cookies",
        "accept cookies", "accept all cookies", "cookies accepteren", "cookies zulassen"
    ];

    private static readonly string[] JsMarkers =
    [
        "enable javascript", "javascript is required", "requires javascript",
        "javascript is disabled", "turn on javascript", "javascript must be enabled",
        "without javascript", "blazor.webassembly.js"
    ];

    public static WebFetchStatus Classify(string rawHtml, string markdown, bool consentElementFound, int minContentChars)
    {
        var readable = ReadableLength(markdown);
        if (readable >= minContentChars)
            return WebFetchStatus.Ok;

        var lower = rawHtml.ToLowerInvariant();

        if (consentElementFound || ConsentMarkers.Any(lower.Contains))
            return WebFetchStatus.BlockedByConsentWall;

        if (JsMarkers.Any(lower.Contains) || EmptyAppRoot().IsMatch(rawHtml))
            return WebFetchStatus.JsRequired;

        if (readable < EmptyChars)
            return ScriptTag().Count(rawHtml) >= 3 ? WebFetchStatus.JsRequired : WebFetchStatus.Empty;

        return WebFetchStatus.Ok;
    }

    /// <summary>
    /// Characters of text a reader would see: link text counts, URLs and Markdown syntax do not.
    /// </summary>
    internal static int ReadableLength(string markdown)
    {
        var text = MarkdownImage().Replace(markdown, " ");
        text = MarkdownLink().Replace(text, "$1");
        text = MarkdownSyntax().Replace(text, " ");
        text = Whitespace().Replace(text, " ");
        return text.Trim().Length;
    }

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"[#*_>|`~=\[\]\-]+")]
    private static partial Regex MarkdownSyntax();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // A single-page-app mount point that the server sent empty: <div id="root"></div> and kin.
    [GeneratedRegex(@"<(div|main)\b[^>]*\bid\s*=\s*[""']?(root|app|__next|__nuxt|svelte)[""']?[^>]*>\s*</\1>|<app-root[^>]*>\s*</app-root>",
        RegexOptions.IgnoreCase)]
    private static partial Regex EmptyAppRoot();

    [GeneratedRegex(@"<script\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();
}
