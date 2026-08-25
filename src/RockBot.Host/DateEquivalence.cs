using System.Globalization;
using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>
/// Credits a merge for re-spelling a date rather than dropping it — "August 19, 2026" merged
/// as "2026-08-19", or the reverse.
/// </summary>
/// <remarks>
/// <para>
/// This was the single largest source of false rejections on a live corpus: "August" alone
/// accounted for 13 of 70 rejections in eight dream cycles, every one of them a merge that
/// normalized a written date into ISO form and kept the day and year intact. Because a
/// rejected merge is re-proposed on the next cycle, each of those was a duplicate cluster
/// pinned open indefinitely.
/// </para>
/// <para>
/// The safe way to fix this is not to add month names to the common-word list. That would
/// unprotect "August" everywhere and forever — including a person named August, a release
/// named August, or a merge that genuinely drops the month. Instead the month stays a required
/// specific and gains one extra way to be satisfied: an equivalent numeric date being present
/// in the merged text. A merge that drops the date in both spellings still fails.
/// </para>
/// <para>
/// Two guards keep the equivalence narrow:
/// </para>
/// <list type="bullet">
/// <item>
/// The month must appear in a date expression <em>in the source</em> — adjacent to a day or a
/// year. A bare "August" with no date around it is not a date and is never credited.
/// </item>
/// <item>
/// When the source's date expression carries a year, the numeric date in the merged text must
/// carry the same one, so "August 2026" is not satisfied by an unrelated "2025-08-04".
/// </item>
/// </list>
/// </remarks>
internal static partial class DateEquivalence
{
    /// <summary>
    /// A month name sitting in a date expression: preceded by a day, or followed by a day
    /// and/or a year. At least one of those groups must match for the occurrence to count.
    /// Full names precede their abbreviations so the alternation prefers the longer form.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:(?<d1>\d{1,2})(?:st|nd|rd|th)?\s*,?\s*)?"
        + @"(?<mon>January|February|March|April|May|June|July|August|September|October|"
        + @"November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sept|Sep|Oct|Nov|Dec)\.?"
        + @"(?:\s*,?\s*(?<d2>\d{1,2})(?:st|nd|rd|th)?)?"
        + @"(?:\s*,?\s*(?<y>\d{4}))?\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex MonthDateExpression();

    /// <summary>Year-first numeric dates: 2026-08-19, 2026/08/19.</summary>
    [GeneratedRegex(@"\b(?<y>\d{4})[-/](?<m>\d{1,2})[-/](?<d>\d{1,2})\b")]
    private static partial Regex IsoNumericDate();

    /// <summary>Month-first numeric dates: 08/19/2026.</summary>
    [GeneratedRegex(@"\b(?<m>\d{1,2})/(?<d>\d{1,2})/(?<y>\d{4})\b")]
    private static partial Regex SlashNumericDate();

    /// <summary>
    /// True when <paramref name="specific"/> is a date component that survived into
    /// <paramref name="merged"/> under a different spelling.
    /// </summary>
    /// <param name="specific">The required specific that failed the plain substring test.</param>
    /// <param name="sourceText">Combined text of the merge's sources, used to guard the equivalence.</param>
    /// <param name="merged">The proposed merged content.</param>
    internal static bool IsCoveredByEquivalentDate(string specific, string sourceText, string merged)
        => MonthNameCoveredByNumericDate(specific, sourceText, merged)
        || NumericDateCoveredByMonthName(specific, merged);

    /// <summary>"August" in the source, "2026-08-19" in the merge.</summary>
    private static bool MonthNameCoveredByNumericDate(string specific, string sourceText, string merged)
    {
        if (MonthNumber(specific) is not { } month)
            return false;

        // Guard: only credit a month that the source actually used as part of a date.
        var years = new HashSet<int>();
        var usedAsDate = false;

        foreach (Match m in MonthDateExpression().Matches(sourceText))
        {
            if (!string.Equals(m.Groups["mon"].Value, specific, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!m.Groups["d1"].Success && !m.Groups["d2"].Success && !m.Groups["y"].Success)
                continue;

            usedAsDate = true;
            if (m.Groups["y"].Success)
                years.Add(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture));
        }

        if (!usedAsDate)
            return false;

        foreach (var (numericMonth, numericYear) in NumericDates(merged))
            if (numericMonth == month && (years.Count == 0 || years.Contains(numericYear)))
                return true;

        return false;
    }

    /// <summary>"2026-08-19" in the source, "August 19, 2026" in the merge.</summary>
    private static bool NumericDateCoveredByMonthName(string specific, string merged)
    {
        // Only a fully-qualified date is unambiguous enough to match by components; a bare
        // "08-19" could be a version, a range or a partial date.
        var iso = IsoNumericDate().Match(specific);
        if (!iso.Success || iso.Length != specific.Length)
            return false;

        var year = int.Parse(iso.Groups["y"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(iso.Groups["m"].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(iso.Groups["d"].Value, CultureInfo.InvariantCulture);

        foreach (Match m in MonthDateExpression().Matches(merged))
        {
            if (MonthNumber(m.Groups["mon"].Value) != month)
                continue;
            if (!m.Groups["y"].Success || int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture) != year)
                continue;

            var dayGroup = m.Groups["d1"].Success ? m.Groups["d1"] : m.Groups["d2"];
            if (dayGroup.Success && int.Parse(dayGroup.Value, CultureInfo.InvariantCulture) == day)
                return true;
        }

        return false;
    }

    private static IEnumerable<(int Month, int Year)> NumericDates(string text)
    {
        foreach (Match m in IsoNumericDate().Matches(text))
            yield return (
                int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture));

        foreach (Match m in SlashNumericDate().Matches(text))
            yield return (
                int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture));
    }

    private static int? MonthNumber(string word) => word.ToLowerInvariant() switch
    {
        "january" or "jan" => 1,
        "february" or "feb" => 2,
        "march" or "mar" => 3,
        "april" or "apr" => 4,
        "may" => 5,
        "june" or "jun" => 6,
        "july" or "jul" => 7,
        "august" or "aug" => 8,
        "september" or "sept" or "sep" => 9,
        "october" or "oct" => 10,
        "november" or "nov" => 11,
        "december" or "dec" => 12,
        _ => null,
    };
}
