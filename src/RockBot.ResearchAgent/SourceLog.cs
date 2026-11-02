using System.Collections.Concurrent;

namespace RockBot.ResearchAgent;

/// <summary>
/// The pages one research run read, in the order it first read them. Filled by
/// <see cref="ResearchToolFunction"/> from successful <c>web_browse</c> calls.
/// </summary>
internal sealed class SourceLog
{
    private readonly ConcurrentDictionary<string, ResearchSource> _byKey = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    /// <summary>Records <paramref name="url"/> unless the same page is already recorded.</summary>
    public void Add(string url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        url = url.Trim();
        var key = DedupKey(url);
        if (_byKey.TryAdd(key, new ResearchSource(url, string.IsNullOrWhiteSpace(title) ? null : title.Trim())))
            _order.Enqueue(key);
    }

    /// <summary>The recorded pages, first read first.</summary>
    public IReadOnlyList<ResearchSource> Sources => [.. _order.Select(k => _byKey[k])];

    /// <summary>
    /// What makes two URLs the same page: scheme and host compare case-insensitively (as they
    /// are), but the path and query keep their case, because <c>/Report</c> and <c>/report</c>
    /// can be different pages. The fragment is dropped: it names a spot on the same page. A
    /// string that isn't an absolute URL is compared as written.
    /// </summary>
    internal static string DedupKey(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant()
              + uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped)
            : url;

    /// <summary>
    /// The page title from a <c>web_browse</c> result: its first Markdown <c># </c> heading,
    /// which the browse tool writes for a page with a title. Null when there is none.
    /// </summary>
    internal static string? TitleFrom(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;
        foreach (var line in content.Split('\n').Take(40))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                return trimmed[2..].Trim() is { Length: > 0 } title ? title : null;
        }
        return null;
    }
}
