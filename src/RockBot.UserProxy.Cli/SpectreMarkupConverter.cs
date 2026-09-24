using System.Text.RegularExpressions;
using RockBot.UserProxy.Rendering;
using Spectre.Console;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Translates a small subset of agent-emitted HTML (color spans, bold,
/// SVG placeholders) into Spectre markup, strips any other known HTML tags
/// (angle-bracket placeholders like <c>&lt;uid&gt;</c> are kept), and escapes
/// the surrounding text so user-supplied <c>[</c> / <c>]</c> can't break the
/// Spectre parser.
/// </summary>
internal static class SpectreMarkupConverter
{
    private const char PlaceholderOpen = '￰';
    private const char PlaceholderClose = '￱';

    private static readonly Regex SvgRegex = new(
        @"<svg\b[^>]*>.*?</svg\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SpanColorRegex = new(
        @"<span\b[^>]*\bstyle\s*=\s*[""']\s*color\s*:\s*([^""';]+?)\s*;?\s*[""'][^>]*>(.*?)</span\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex BoldRegex = new(
        @"<(strong|b)\b[^>]*>(.*?)</\1\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex PlaceholderRegex = new(
        $"{PlaceholderOpen}(\\d+){PlaceholderClose}",
        RegexOptions.Compiled);

    private static readonly Regex HexColorRegex = new(
        @"^#[0-9a-fA-F]{6}$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "red", "green", "blue", "yellow", "cyan", "magenta", "grey", "gray", "white"
    };

    public static string ToSpectreMarkup(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        var placeholders = new List<string>();

        string Stash(string token)
        {
            var index = placeholders.Count;
            placeholders.Add(token);
            return $"{PlaceholderOpen}{index}{PlaceholderClose}";
        }

        var stage = SvgRegex.Replace(input, _ => Stash(Markup.Escape("[chart — view in Blazor]")));

        stage = SpanColorRegex.Replace(stage, m =>
        {
            var color = m.Groups[1].Value.Trim();
            var innerText = KnownMarkupTags.StripKnownTags(m.Groups[2].Value);
            var escapedInner = Markup.Escape(innerText);

            if (NamedColors.Contains(color))
                return Stash($"[{color.ToLowerInvariant()}]{escapedInner}[/]");

            if (HexColorRegex.IsMatch(color))
                return Stash($"[{color.ToLowerInvariant()}]{escapedInner}[/]");

            return innerText;
        });

        stage = BoldRegex.Replace(stage, m =>
        {
            var innerText = KnownMarkupTags.StripKnownTags(m.Groups[2].Value);
            return Stash($"[bold]{Markup.Escape(innerText)}[/]");
        });

        stage = KnownMarkupTags.StripKnownTags(stage);

        var escaped = Markup.Escape(stage);

        return PlaceholderRegex.Replace(escaped, m =>
        {
            var idx = int.Parse(m.Groups[1].Value);
            return placeholders[idx];
        });
    }
}
