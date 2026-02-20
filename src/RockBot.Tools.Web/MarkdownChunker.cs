namespace RockBot.Tools.Web;

/// <summary>
/// Splits a Markdown document into heading-based chunks with a max-size fallback.
/// </summary>
internal static class MarkdownChunker
{
    /// <summary>
    /// Splits <paramref name="markdown"/> into chunks no larger than <paramref name="maxLength"/>.
    /// Chunks are split at H1/H2/H3 heading boundaries first; oversized sections are further
    /// split at blank lines, and hard-split at <paramref name="maxLength"/> as a last resort.
    /// </summary>
    /// <param name="markdown">The Markdown content to chunk.</param>
    /// <param name="maxLength">Maximum character length of each chunk.</param>
    /// <returns>A list of (Heading, Content) pairs.</returns>
    public static IReadOnlyList<(string Heading, string Content)> Chunk(string markdown, int maxLength)
    {
        var sections = SplitAtHeadings(markdown);
        var result = new List<(string Heading, string Content)>();

        foreach (var (heading, content) in sections)
        {
            if (content.Length <= maxLength)
            {
                result.Add((heading, content));
                continue;
            }

            // Section is too large — split at blank lines first
            var subChunks = SplitAtBlankLines(content, maxLength);
            var chunkIndex = 0;
            foreach (var chunk in subChunks)
            {
                var subHeading = chunkIndex == 0 ? heading : $"{heading} (continued {chunkIndex})";
                result.Add((subHeading, chunk));
                chunkIndex++;
            }
        }

        return result;
    }

    /// <summary>Splits markdown at H1/H2/H3 heading lines into (heading, content) pairs.</summary>
    private static List<(string Heading, string Content)> SplitAtHeadings(string markdown)
    {
        var result = new List<(string Heading, string Content)>();
        var lines = markdown.Split('\n');
        var currentHeading = string.Empty;
        var currentContent = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (IsHeading(line))
            {
                var accumulated = currentContent.ToString().Trim();
                // Only emit the current section if it has content (skip empty pre-heading preamble)
                if (accumulated.Length > 0)
                    result.Add((currentHeading, accumulated));

                currentHeading = line.TrimStart('#').Trim();
                currentContent.Clear();
            }
            else
            {
                currentContent.Append(line);
                currentContent.Append('\n');
            }
        }

        // Always emit the final section; if nothing was added yet (empty doc or heading-only), emit one entry
        var lastContent = currentContent.ToString().Trim();
        result.Add((currentHeading, lastContent));

        return result;
    }

    private static bool IsHeading(string line)
    {
        if (line.StartsWith("### ", StringComparison.Ordinal)) return true;
        if (line.StartsWith("## ", StringComparison.Ordinal)) return true;
        if (line.StartsWith("# ", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into chunks no larger than <paramref name="maxLength"/>,
    /// preferring blank-line boundaries. Falls back to hard-splitting at <paramref name="maxLength"/>.
    /// </summary>
    private static List<string> SplitAtBlankLines(string text, int maxLength)
    {
        var result = new List<string>();
        var paragraphs = text.Split("\n\n");
        var current = new System.Text.StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            var addition = current.Length == 0
                ? paragraph
                : "\n\n" + paragraph;

            if (current.Length + addition.Length > maxLength && current.Length > 0)
            {
                // Flush current chunk
                var chunk = current.ToString().Trim();
                if (chunk.Length > 0)
                    result.AddRange(HardSplit(chunk, maxLength));
                current.Clear();
                current.Append(paragraph);
            }
            else
            {
                current.Append(addition);
            }
        }

        if (current.Length > 0)
        {
            var remaining = current.ToString().Trim();
            if (remaining.Length > 0)
                result.AddRange(HardSplit(remaining, maxLength));
        }

        return result;
    }

    /// <summary>Hard-splits <paramref name="text"/> at exactly <paramref name="maxLength"/> chars.</summary>
    private static IEnumerable<string> HardSplit(string text, int maxLength)
    {
        for (var i = 0; i < text.Length; i += maxLength)
            yield return text.Substring(i, Math.Min(maxLength, text.Length - i));
    }
}
