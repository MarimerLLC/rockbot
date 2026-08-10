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
    /// Extracts the load-bearing specifics from a piece of memory content: proper nouns,
    /// acronyms, and numbers.
    /// </summary>
    /// <param name="content">Memory content to scan.</param>
    /// <param name="vocabulary">
    /// Which words count as ordinary language. Defaults to the generic-English baseline;
    /// deployments override it via <c>merge-coverage-vocabulary.json</c>.
    /// </param>
    internal static HashSet<string> ExtractSpecifics(
        string? content,
        MergeCoverageVocabulary? vocabulary = null)
    {
        var words = vocabulary ?? MergeCoverageVocabulary.Default;

        var specifics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content))
            return specifics;

        foreach (Match m in CapitalizedWord().Matches(content))
        {
            var word = Possessive().Replace(m.Value, string.Empty);
            if (word.Length > 2 && !words.IsCommon(word))
                specifics.Add(word);
        }

        foreach (Match m in Acronym().Matches(content))
            if (!words.IsCommon(m.Value))
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
        string? mergedContent,
        MergeCoverageVocabulary? vocabulary = null)
    {
        var merged = mergedContent ?? string.Empty;

        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            required.UnionWith(ExtractSpecifics(source.Content, vocabulary));

        if (required.Count == 0)
            return [];

        return [.. required
            .Where(s => merged.IndexOf(s, StringComparison.OrdinalIgnoreCase) < 0)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
    }
}
