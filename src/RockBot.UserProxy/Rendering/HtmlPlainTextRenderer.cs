using System.Text.RegularExpressions;

namespace RockBot.UserProxy.Rendering;

/// <summary>
/// Strips HTML / SVG markup from agent replies so text-only frontends (CLI,
/// future WhatsApp / Discord proxies) never render literal tags. Acts as a
/// safety net for the honour-system <c>ClientCapabilities</c> gate: even if a
/// scheduled task fires with <c>outputFormat: "rich"</c> while a plain-text
/// client is connected, the user sees readable text instead of raw markup.
/// </summary>
public static class HtmlPlainTextRenderer
{
    private static readonly Regex SvgRegex = new(
        @"<svg\b[^>]*>.*?</svg\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex TagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled);

    /// <summary>
    /// Replaces <c>&lt;svg&gt;...&lt;/svg&gt;</c> blocks with a chart placeholder
    /// and removes any other HTML tags. Newlines and whitespace are preserved;
    /// null / empty input passes through unchanged.
    /// </summary>
    public static string StripHtml(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        var withoutSvg = SvgRegex.Replace(input, "[chart — view in Blazor]");
        return TagRegex.Replace(withoutSvg, string.Empty);
    }
}
