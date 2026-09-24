using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace RockBot.UserProxy.Rendering;

/// <summary>
/// The set of HTML / SVG / MathML element names that renderers treat as real
/// markup. Anything else in angle brackets — <c>&lt;folder&gt;</c>,
/// <c>&lt;uid&gt;</c>, <c>&lt;id&gt;</c> — is a placeholder in prose (typically
/// quoted from a tool error or usage string) and must be shown literally rather
/// than stripped as if it were a tag.
/// </summary>
/// <remarks>
/// Dangerous elements (<c>script</c>, <c>style</c>, <c>iframe</c>, …) are
/// deliberately included: being "known" means they keep being treated as markup
/// and removed, never surfaced as text.
/// </remarks>
public static class KnownMarkupTags
{
    private static readonly FrozenSet<string> Names = new[]
    {
        // HTML living standard
        "a", "abbr", "address", "area", "article", "aside", "audio",
        "b", "base", "bdi", "bdo", "blockquote", "body", "br", "button",
        "canvas", "caption", "cite", "code", "col", "colgroup",
        "data", "datalist", "dd", "del", "details", "dfn", "dialog", "div", "dl", "dt",
        "em", "embed",
        "fieldset", "figcaption", "figure", "footer", "form",
        "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hgroup", "hr", "html",
        "i", "iframe", "img", "input", "ins",
        "kbd",
        "label", "legend", "li", "link",
        "main", "map", "mark", "menu", "meta", "meter",
        "nav", "noscript",
        "object", "ol", "optgroup", "option", "output",
        "p", "param", "picture", "pre", "progress",
        "q",
        "rp", "rt", "ruby",
        "s", "samp", "script", "search", "section", "select", "slot", "small", "source",
        "span", "strong", "style", "sub", "summary", "sup",
        "table", "tbody", "td", "template", "textarea", "tfoot", "th", "thead", "time",
        "title", "tr", "track",
        "u", "ul",
        "var", "video",
        "wbr",

        // Obsolete / non-standard HTML still recognised by browsers
        "acronym", "applet", "basefont", "bgsound", "big", "blink", "center", "dir",
        "font", "frame", "frameset", "image", "isindex", "keygen", "listing", "marquee",
        "menuitem", "multicol", "nextid", "nobr", "noembed", "noframes", "plaintext",
        "rb", "rtc", "spacer", "strike", "tt", "xmp",

        // SVG
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "textPath", "desc", "defs", "marker", "symbol", "use",
        "linearGradient", "radialGradient", "stop", "pattern", "clipPath", "mask",
        "filter", "foreignObject", "switch", "view", "animate", "animateMotion",
        "animateTransform", "set", "mpath",
        "feBlend", "feColorMatrix", "feComponentTransfer", "feComposite",
        "feConvolveMatrix", "feDiffuseLighting", "feDisplacementMap", "feDistantLight",
        "feDropShadow", "feFlood", "feFuncA", "feFuncB", "feFuncG", "feFuncR",
        "feGaussianBlur", "feImage", "feMerge", "feMergeNode", "feMorphology",
        "feOffset", "fePointLight", "feSpecularLighting", "feSpotLight", "feTile",
        "feTurbulence",

        // MathML root
        "math",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // A start or end tag: optional '/', a tag name, then either the end of the tag
    // or whitespace / '/' before attributes.
    private static readonly Regex TagRegex = new(
        @"<(/?)([A-Za-z][A-Za-z0-9-]*)(?=[\s/>])[^>]*>",
        RegexOptions.Compiled);

    // HTML comments and <!DOCTYPE …> / <![CDATA[ … ]]> declarations.
    private static readonly Regex CommentOrDeclarationRegex = new(
        @"<!--.*?-->|<![^>]*>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Returns true when <paramref name="name"/> is a real HTML / SVG / MathML element name.</summary>
    public static bool IsKnown(string name) => Names.Contains(name);

    /// <summary>
    /// If <paramref name="tag"/> is a single start or end tag (e.g. <c>&lt;folder&gt;</c>,
    /// <c>&lt;/b&gt;</c>, <c>&lt;span class="x"&gt;</c>), returns its element name.
    /// Returns null for comments, declarations, processing instructions, or
    /// anything else that isn't an element tag.
    /// </summary>
    public static string? GetTagName(string tag)
    {
        var m = TagRegex.Match(tag);
        return m.Success && m.Index == 0 ? m.Groups[2].Value : null;
    }

    /// <summary>
    /// Removes known HTML / SVG tags, comments and declarations from
    /// <paramref name="input"/>, keeping their inner text. Unknown
    /// <c>&lt;placeholder&gt;</c> tokens are left untouched.
    /// </summary>
    public static string StripKnownTags(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var withoutComments = CommentOrDeclarationRegex.Replace(input, string.Empty);
        return TagRegex.Replace(withoutComments, m => IsKnown(m.Groups[2].Value) ? string.Empty : m.Value);
    }
}
