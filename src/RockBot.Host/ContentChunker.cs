namespace RockBot.Host;

/// <summary>
/// Splits text content into chunks with a configurable maximum size.
/// Prefers splitting at Markdown H1/H2/H3 heading boundaries, falls back to
/// blank-line boundaries, and hard-splits as a last resort. Works correctly
/// with both Markdown documents and arbitrary text (JSON, plain text, etc.).
/// </summary>
public static class ContentChunker
{
    /// <summary>
    /// Splits <paramref name="content"/> into chunks no larger than <paramref name="maxLength"/>.
    /// Chunks are split at H1/H2/H3 heading boundaries first; oversized sections are further
    /// split at blank lines, and hard-split at <paramref name="maxLength"/> as a last resort.
    /// </summary>
    /// <param name="content">The content to chunk.</param>
    /// <param name="maxLength">Maximum character length of each chunk.</param>
    /// <returns>A list of (Heading, Content) pairs.</returns>
    public static IReadOnlyList<(string Heading, string Content)> Chunk(string content, int maxLength)
    {
        var sections = SplitAtHeadings(content);
        var result = new List<(string Heading, string Content)>();

        foreach (var (heading, body) in sections)
        {
            if (body.Length <= maxLength)
            {
                result.Add((heading, body));
                continue;
            }

            // Section is too large — split at blank lines first
            var subChunks = SplitAtBlankLines(body, maxLength);
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

    /// <summary>Splits content at H1/H2/H3 heading lines into (heading, content) pairs.</summary>
    private static List<(string Heading, string Content)> SplitAtHeadings(string content)
    {
        var result = new List<(string Heading, string Content)>();
        var lines = content.Split('\n');
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
