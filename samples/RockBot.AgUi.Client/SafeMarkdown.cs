using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RockBot.AgUi.Client;

/// <summary>
/// A deliberately small Markdown renderer for the sample: paragraphs, headings, block quotes,
/// lists, fenced code, bold, italic, inline code and links. Enough for the capabilities the
/// sample declares (<see cref="ChatSessionOptions.Capabilities"/>), and no more.
/// </summary>
/// <remarks>
/// Safe by construction, not by sanitizing: every character of the input is HTML-encoded before
/// any markup is added, and the only markup added is the fixed set of tags below. HTML in a
/// reply therefore shows as text. A link is made only for an <c>http</c> or <c>https</c> address.
/// A real client would use a Markdown library and an HTML sanitizer, as the Blazor UI does.
/// </remarks>
public static partial class SafeMarkdown
{
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return "";

        var html = new StringBuilder();
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            html.Append("<p>").AppendJoin("<br>", paragraph.Select(Inline)).Append("</p>");
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++)
                    code.Add(lines[i]);
                html.Append("<pre><code>").Append(WebUtility.HtmlEncode(string.Join("\n", code))).Append("</code></pre>");
            }
            else if (trimmed.Length == 0)
            {
                FlushParagraph();
            }
            else if (Heading().Match(trimmed) is { Success: true } heading)
            {
                FlushParagraph();
                // h3 to h5: a reply's "# Title" is a heading inside a chat bubble, not the page's.
                var level = heading.Groups[1].Length + 2;
                html.Append($"<h{level}>").Append(Inline(heading.Groups[2].Value)).Append($"</h{level}>");
            }
            else if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quoted = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++)
                    quoted.Add(lines[i].TrimStart()[1..].TrimStart());
                i--;
                html.Append("<blockquote>").AppendJoin("<br>", quoted.Select(Inline)).Append("</blockquote>");
            }
            else if (ListItem().Match(trimmed) is { Success: true } first)
            {
                FlushParagraph();
                var tag = first.Groups[1].Value.Length > 1 ? "ol" : "ul";
                html.Append('<').Append(tag).Append('>');
                for (; i < lines.Length && ListItem().Match(lines[i].TrimStart()) is { Success: true } item; i++)
                    html.Append("<li>").Append(Inline(item.Groups[2].Value)).Append("</li>");
                i--;
                html.Append("</").Append(tag).Append('>');
            }
            else
            {
                paragraph.Add(trimmed);
            }
        }

        FlushParagraph();
        return html.ToString();
    }

    /// <summary>One line's inline markup. Text between backticks is code and gets none of the rest.</summary>
    private static string Inline(string text)
    {
        var html = new StringBuilder();
        var parts = text.Split('`');
        for (var i = 0; i < parts.Length; i++)
        {
            var encoded = WebUtility.HtmlEncode(parts[i]);
            // An odd part sits between two backticks, unless the last backtick was never closed.
            if (i % 2 == 1 && i < parts.Length - 1)
            {
                html.Append("<code>").Append(encoded).Append("</code>");
                continue;
            }
            if (i % 2 == 1)
                html.Append('`');

            // The text is already encoded, so a quote in an address is &quot; and can't end the
            // attribute, and the address must start with http(s): no javascript: links.
            encoded = Link().Replace(encoded, "<a href=\"$2\" target=\"_blank\" rel=\"noopener noreferrer\">$1</a>");
            encoded = Bold().Replace(encoded, "<strong>$1</strong>");
            encoded = Italic().Replace(encoded, "<em>$1</em>");
            html.Append(encoded);
        }
        return html.ToString();
    }

    [GeneratedRegex(@"^(#{1,3})\s+(.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^([-*+]|\d+\.)\s+(.+)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^\s)]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])")]
    private static partial Regex Italic();
}
