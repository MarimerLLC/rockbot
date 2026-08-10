using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>
/// Checks that a consolidated memory entry still carries the specifics its sources carried.
/// </summary>
/// <remarks>
/// <para>
/// A merge is the only place in the pipeline where stored text is regenerated rather than
/// added or removed, and the regenerated prose has no anchor back to what the sources said.
/// The characteristic failure is not a wrong merge, it is a plausible one: an entry recording
/// "Rocky Lhotka also appears in travel and calendar data as Rockford Duane Lhotka, default
/// timezone America/Chicago, accounts …" merges into something that keeps the account list —
/// the machine-readable part — and quietly drops the name. The result reads fine. Nothing
/// flags it. The sources are gone.
/// </para>
/// <para>
/// So the check is deliberately mechanical and deliberately biased toward rejection: names,
/// numbers and dates present in a source must appear in the merged text, or the merge does not
/// happen and the sources are left alone. The cost of a false rejection is a duplicate pair
/// surviving another cycle; the cost of a false acceptance is a fact nobody can recover the
/// wording of. Those are not symmetric.
/// </para>
/// </remarks>
internal static partial class MergeCoverage
{
    [GeneratedRegex(@"\b\p{Lu}[\p{L}']{2,}\b")]
    private static partial Regex CapitalizedWord();

    [GeneratedRegex(@"\b\p{Lu}{2,}\b")]
    private static partial Regex Acronym();

    /// <summary>
    /// Numbers, including dates, times, versions and decimals. Two characters minimum: a bare
    /// digit is far more often incidental ("the top 3 items") than load-bearing, and requiring
    /// it to survive would reject merges that legitimately rephrase to "three".
    /// </summary>
    [GeneratedRegex(@"\d(?:[.,:/\-]?\d)+")]
    private static partial Regex Numeric();

    /// <summary>Trailing possessive, so a source's "Rocky's" is satisfied by "Rocky".</summary>
    [GeneratedRegex(@"['’]s?$")]
    private static partial Regex Possessive();

    /// <summary>
    /// Common words that show up capitalized purely because they start a sentence. Matching is
    /// case-insensitive, so this also spares ordinary mid-sentence prose from being mistaken
    /// for a proper noun.
    /// </summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "this", "that", "these", "those", "there", "then", "than", "and", "but", "not",
        "are", "was", "were", "has", "have", "had", "its", "his", "her", "their", "they", "them",
        "also", "only", "just", "such", "each", "both", "all", "any", "one", "two", "three",
        "new", "now", "use", "uses", "used", "using", "should", "must", "may", "can", "will",
        "would", "could", "note", "example", "include", "includes", "including", "prefer",
        "prefers", "avoid", "does", "did", "set", "get", "save", "send", "read", "write", "run",
        "when", "what", "where", "while", "with", "from", "for", "into", "over", "under",
        "about", "after", "before", "during", "between", "because", "however", "instead",
        "since", "until", "upon", "user", "agent", "detail", "details", "task", "tasks", "item",
        "items", "entry", "entries", "memory", "working", "long", "term", "data", "time",
        "date", "day", "days", "week", "weeks", "month", "months", "year", "years", "ago",
        "per", "via", "context", "current", "currently", "still", "already", "always", "never",
        "other", "another", "same", "different", "first", "second", "last", "next", "previous",
        "some", "most", "many", "much", "more", "less", "very", "rather", "quite",
    };

    /// <summary>
    /// Extracts the load-bearing specifics from a piece of memory content: proper nouns,
    /// acronyms, and numbers.
    /// </summary>
    internal static HashSet<string> ExtractSpecifics(string? content)
    {
        var specifics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content))
            return specifics;

        foreach (Match m in CapitalizedWord().Matches(content))
        {
            var word = Possessive().Replace(m.Value, string.Empty);
            if (word.Length > 2 && !CommonWords.Contains(word))
                specifics.Add(word);
        }

        foreach (Match m in Acronym().Matches(content))
            if (!CommonWords.Contains(m.Value))
                specifics.Add(m.Value);

        foreach (Match m in Numeric().Matches(content))
            specifics.Add(m.Value);

        return specifics;
    }

    /// <summary>
    /// Returns the specifics present across <paramref name="sources"/> that do not survive into
    /// <paramref name="mergedContent"/>, sorted for stable logging. An empty result means the
    /// merge is safe to apply.
    /// </summary>
    /// <remarks>
    /// Matching is a case-insensitive substring test rather than a token test, so a merge is
    /// credited for reformatting — "Rockford Duane Lhotka" still covers the source token
    /// "Rockford", and "2026-09-10" covers "2026". Reformatting that genuinely discards a
    /// component (spelling out "September" as "09") is reported as missing, which is the
    /// conservative direction.
    /// </remarks>
    internal static IReadOnlyList<string> FindMissingSpecifics(
        IEnumerable<MemoryEntry> sources,
        string? mergedContent)
    {
        var merged = mergedContent ?? string.Empty;

        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            required.UnionWith(ExtractSpecifics(source.Content));

        if (required.Count == 0)
            return [];

        return [.. required
            .Where(s => merged.IndexOf(s, StringComparison.OrdinalIgnoreCase) < 0)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
    }
}
