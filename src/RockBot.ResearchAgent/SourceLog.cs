using System.Collections.Concurrent;

namespace RockBot.ResearchAgent;

/// <summary>
/// The pages one research run read, in the order it first read them. Filled by
/// <see cref="ResearchToolFunction"/> from successful <c>web_browse</c> calls.
/// </summary>
internal sealed class SourceLog
{
    private readonly ConcurrentDictionary<string, ResearchSource> _byUrl = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _order = new();

    /// <summary>Records <paramref name="url"/> unless it is already recorded.</summary>
    public void Add(string url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        url = url.Trim();
        if (_byUrl.TryAdd(url, new ResearchSource(url, string.IsNullOrWhiteSpace(title) ? null : title.Trim())))
            _order.Enqueue(url);
    }

    /// <summary>The recorded pages, first read first.</summary>
    public IReadOnlyList<ResearchSource> Sources => [.. _order.Select(u => _byUrl[u])];

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
