using System.Text;
using System.Text.RegularExpressions;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Best-effort RFC 6570 matching of a concrete URI against the resources and templates a server
/// declares (#617). Ported from mcp-aggregator#46. It only decides whether a URI is one the server
/// told us about — the server itself does the real expansion when the URI is read — so it errs
/// towards accepting: every expression matches the characters its operator could produce.
/// </summary>
public static class McpResourceUriTemplate
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A regex matching the URIs <paramref name="uriTemplate"/> could expand to. Literal text is
    /// matched exactly; an unbalanced <c>{</c> makes the rest of the template literal.
    /// </summary>
    public static Regex BuildMatcher(string uriTemplate)
    {
        ArgumentNullException.ThrowIfNull(uriTemplate);

        var pattern = new StringBuilder("^");
        var i = 0;
        while (i < uriTemplate.Length)
        {
            var open = uriTemplate.IndexOf('{', i);
            if (open < 0)
            {
                pattern.Append(Regex.Escape(uriTemplate[i..]));
                break;
            }

            var close = uriTemplate.IndexOf('}', open + 1);
            if (close < 0)
            {
                pattern.Append(Regex.Escape(uriTemplate[i..]));
                break;
            }

            pattern.Append(Regex.Escape(uriTemplate[i..open]));
            var expression = uriTemplate[(open + 1)..close];
            var op = expression.Length > 0 ? expression[0] : '\0';
            pattern.Append(op switch
            {
                '+' => ".+",
                '#' => "(?:#.*)?",
                '/' => "(?:/[^/?#]+)*",
                '?' or '&' => "(?:[?&][^#]*)?",
                '.' => @"(?:\.[^/?#]+)?",
                ';' => "(?:;[^/?#]*)?",
                _ => "[^/?#]+",
            });
            i = close + 1;
        }

        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant, MatchTimeout);
    }

    /// <summary>True when <paramref name="uri"/> expands <paramref name="uriTemplate"/>.</summary>
    public static bool Matches(string uriTemplate, string uri)
    {
        try
        {
            return BuildMatcher(uriTemplate).IsMatch(uri);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// The declared resource or template <paramref name="uri"/> refers to: an exact resource URI
    /// first, then the first template that matches; null when it is neither.
    /// </summary>
    public static McpResourceDefinition? Resolve(IEnumerable<McpResourceDefinition> declared, string uri)
    {
        var list = declared as IReadOnlyCollection<McpResourceDefinition> ?? [.. declared];
        return list.FirstOrDefault(d => !d.IsTemplate && string.Equals(d.Uri, uri, StringComparison.Ordinal))
            ?? list.FirstOrDefault(d => d.IsTemplate && Matches(d.Uri, uri));
    }
}
