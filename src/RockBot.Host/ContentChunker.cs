using System.Text;

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
    /// <returns>A list of <see cref="ChunkInfo"/> entries.</returns>
    public static IReadOnlyList<ChunkInfo> Chunk(string content, int maxLength)
    {
        var sections = SplitAtHeadings(content);
        var result = new List<ChunkInfo>();

        foreach (var section in sections)
        {
            if (section.Content.Length <= maxLength)
            {
                result.Add(section);
                continue;
            }

            // Section is too large — split at blank lines first
            var subChunks = SplitAtBlankLines(section.Content, maxLength);
            var chunkIndex = 0;
            foreach (var chunk in subChunks)
            {
                var subHeading = chunkIndex == 0 ? section.Heading : $"{section.Heading} (continued {chunkIndex})";
                result.Add(new ChunkInfo(subHeading, chunk, section.HeadingLevel));
                chunkIndex++;
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a hierarchical markdown outline from chunked content, mapping each
    /// section heading to its working-memory key. Suitable for storing as an index
    /// chunk so the agent can navigate large documents without loading every chunk.
    /// </summary>
    /// <param name="chunks">The chunks returned by <see cref="Chunk"/>.</param>
    /// <param name="chunkKeys">Parallel list of working-memory keys, one per chunk.</param>
    /// <returns>A markdown outline string with indented headings and chunk keys.</returns>
    public static string BuildOutline(IReadOnlyList<ChunkInfo> chunks, IReadOnlyList<string> chunkKeys)
    {
        if (chunks.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("## Document Outline");
        sb.AppendLine();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var key = chunkKeys[i];
            var label = string.IsNullOrWhiteSpace(chunk.Heading) ? $"Part {i}" : chunk.Heading;

            // Indent based on heading level: H1 = no indent, H2 = 2 spaces, H3 = 4 spaces, none = no indent
            var indent = chunk.HeadingLevel switch
            {
                2 => "  ",
                3 => "    ",
                _ => ""
            };

            sb.AppendLine($"{indent}- **{label}** → `{key}`");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Splits content at H1/H2/H3 heading lines into <see cref="ChunkInfo"/> entries.</summary>
    private static List<ChunkInfo> SplitAtHeadings(string content)
    {
        var result = new List<ChunkInfo>();
        var lines = content.Split('\n');
        var currentHeading = string.Empty;
        var currentLevel = 0;
        var currentContent = new StringBuilder();

        foreach (var line in lines)
        {
            var level = GetHeadingLevel(line);
            if (level > 0)
            {
                var accumulated = currentContent.ToString().Trim();
                // Only emit the current section if it has content (skip empty pre-heading preamble)
                if (accumulated.Length > 0)
                    result.Add(new ChunkInfo(currentHeading, accumulated, currentLevel));

                currentHeading = line.TrimStart('#').Trim();
                currentLevel = level;
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
        result.Add(new ChunkInfo(currentHeading, lastContent, currentLevel));

        return result;
    }

    /// <summary>Returns the heading level (1, 2, or 3) or 0 if not a heading.</summary>
    private static int GetHeadingLevel(string line)
    {
        if (line.StartsWith("### ", StringComparison.Ordinal)) return 3;
        if (line.StartsWith("## ", StringComparison.Ordinal)) return 2;
        if (line.StartsWith("# ", StringComparison.Ordinal)) return 1;
        return 0;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into chunks no larger than <paramref name="maxLength"/>,
    /// preferring blank-line boundaries. Falls back to hard-splitting at <paramref name="maxLength"/>.
    /// </summary>
    private static List<string> SplitAtBlankLines(string text, int maxLength)
    {
        var result = new List<string>();
        var paragraphs = text.Split("\n\n");
        var current = new StringBuilder();

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

/// <summary>
/// Represents a single chunk of content with its heading text and heading level.
/// </summary>
/// <param name="Heading">The heading text (without # prefix), or empty for untitled sections.</param>
/// <param name="Content">The chunk content.</param>
/// <param name="HeadingLevel">The markdown heading level (1 for H1, 2 for H2, 3 for H3, 0 for no heading).</param>
public sealed record ChunkInfo(string Heading, string Content, int HeadingLevel);
