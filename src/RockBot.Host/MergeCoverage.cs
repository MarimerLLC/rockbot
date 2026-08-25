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
/// <para>
/// That bias has a cost of its own, and it is not free the way it first looks. A rejected
/// merge is re-proposed next cycle, so a false rejection is not one wasted merge — it is a
/// duplicate cluster that never resolves and burns a slot on every future cycle. A live corpus
/// showed one six-source merge rejected five times in eight cycles. Two classes of false
/// rejection are addressed here, both by widening what counts as *covering* a specific rather
/// than by dropping the requirement:
/// </para>
/// <list type="bullet">
/// <item>
/// Sentence position. A capitalized word is only evidence of a proper noun when it is not
/// sentence-initial. Consulting the common-word list only in that position keeps "Personal"
/// protected in "OneDrive Personal" while letting "Valid email-capable IDs …" pass.
/// </item>
/// <item>
/// Date spelling. "August 19, 2026" and "2026-08-19" are the same fact. Crediting one for the
/// other is guarded on the month appearing in a date expression in the source, so a bare
/// "August" — or a person named August — stays required.
/// </item>
/// </list>
/// <para>
/// The distinction matters: adding a word to the common list unprotects it everywhere, in
/// every context, permanently. Widening the coverage test leaves it required and can only be
/// satisfied by an equivalent form actually being present, so a merge that drops both forms
/// is still rejected.
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
            if (word.Length <= 2)
                continue;

            // The generic-English baseline exists to absorb words capitalized only because they
            // open a sentence, so it only applies there. Mid-sentence capitalization is evidence
            // of a proper noun: "Personal", "Class" and "Benefit" are ordinary at a sentence
            // start and load-bearing in "OneDrive Personal", "Blazor Online Class" and "MVP
            // Azure Extended Benefit". A deployment's own extraCommonWords still applies in
            // every position -- see MergeCoverageVocabulary.IsCommon.
            var applyBaseline = SentencePosition.IsSentenceInitial(content, m.Index);
            if (words.IsCommon(word, applyBaseline))
                continue;

            specifics.Add(word);
        }

        // Deliberately not position-aware. An all-caps token is not capitalized by grammar, so
        // opening a sentence tells us nothing about it -- and the live corpus showed acronym
        // drops to be real losses ("LLC" went missing alongside Microsoft, Google, ICS and IMAP
        // in a merge that discarded the whole account-provider map).
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
    /// "Rockford", and "2026-09-10" covers "2026". A specific that survives only as an
    /// equivalent date spelling is credited by <see cref="DateEquivalence"/>; anything else
    /// that fails the substring test is reported as missing, which is the conservative
    /// direction.
    /// </remarks>
    internal static IReadOnlyList<string> FindMissingSpecifics(
        IEnumerable<MemoryEntry> sources,
        string? mergedContent,
        MergeCoverageVocabulary? vocabulary = null)
    {
        var merged = mergedContent ?? string.Empty;

        var materialized = sources as IReadOnlyCollection<MemoryEntry> ?? [.. sources];

        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in materialized)
            required.UnionWith(ExtractSpecifics(source.Content, vocabulary));

        if (required.Count == 0)
            return [];

        var sourceText = string.Join("\n", materialized.Select(s => s.Content));

        return [.. required
            .Where(s => !IsCovered(s, sourceText, merged))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsCovered(string specific, string sourceText, string merged) =>
        merged.IndexOf(specific, StringComparison.OrdinalIgnoreCase) >= 0
        || DateEquivalence.IsCoveredByEquivalentDate(specific, sourceText, merged);
}
