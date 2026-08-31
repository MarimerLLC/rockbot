namespace RockBot.Host;

/// <summary>
/// Splits a markdown document on <c>## </c> headings into an <see cref="AgentProfileDocument"/>.
/// A <c>#</c> title flows into the preamble; only <c>##</c> headings delimit sections.
/// </summary>
internal static class ProfileMarkdownParser
{
    private const string SectionPrefix = "## ";

    /// <summary>
    /// Parses raw markdown content into an <see cref="AgentProfileDocument"/>.
    /// </summary>
    public static AgentProfileDocument Parse(string documentType, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentType);
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0)
        {
            return new AgentProfileDocument(documentType, null, [], content);
        }

        var lines = content.Split('\n');
        var sections = new List<AgentProfileSection>();
        var preambleLines = new List<string>();
        string? currentHeading = null;
        var currentBody = new List<string>();
        var inPreamble = true;

        foreach (var line in lines)
        {
            if (line.StartsWith(SectionPrefix, StringComparison.Ordinal))
            {
                if (!inPreamble && currentHeading is not null)
                {
                    sections.Add(BuildSection(currentHeading, currentBody));
                }

                currentHeading = line[SectionPrefix.Length..].TrimEnd();
                currentBody.Clear();
                inPreamble = false;
            }
            else if (inPreamble)
            {
                preambleLines.Add(line);
            }
            else
            {
                currentBody.Add(line);
            }
        }

        // Flush last section
        if (!inPreamble && currentHeading is not null)
        {
            sections.Add(BuildSection(currentHeading, currentBody));
        }

        var preamble = preambleLines.Count > 0
            ? string.Join('\n', preambleLines).Trim()
            : null;

        // Treat whitespace-only preamble as absent
        if (string.IsNullOrWhiteSpace(preamble))
            preamble = null;

        return new AgentProfileDocument(documentType, preamble, sections, content);
    }

    private static AgentProfileSection BuildSection(string heading, List<string> bodyLines)
    {
        var body = string.Join('\n', bodyLines).Trim();
        return new AgentProfileSection(heading, body);
    }
}
