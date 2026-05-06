using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>
/// Runs a caller-supplied regex pattern across a pre-filtered set of <see cref="MemoryEntry"/>
/// candidates. Used by the regex backend of <see cref="ILongTermMemory.SearchAsync"/>.
/// Bounds cost two ways: a per-entry <see cref="Regex.MatchTimeout"/> catches catastrophic
/// backtracking on a single input, and an overall wall-clock budget across the scan stops a
/// slow-but-not-pathological pattern from dominating as the corpus grows.
/// </summary>
internal static class RegexMatcher
{
    internal static readonly TimeSpan DefaultPerEntryTimeout = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan DefaultOverallBudget = TimeSpan.FromSeconds(10);
    internal const int MaxPatternLength = 512;

    public static IReadOnlyList<MemoryEntry> MatchEntries(
        IReadOnlyCollection<MemoryEntry> candidates,
        string pattern,
        bool caseSensitive,
        int maxResults,
        Func<MemoryEntry, string> documentText) =>
        MatchEntries(candidates, pattern, caseSensitive, maxResults, documentText,
            DefaultPerEntryTimeout, DefaultOverallBudget);

    /// <summary>
    /// Test-friendly overload that allows custom timeouts. Production callers should use
    /// the parameterless-budget overload above.
    /// </summary>
    internal static IReadOnlyList<MemoryEntry> MatchEntries(
        IReadOnlyCollection<MemoryEntry> candidates,
        string pattern,
        bool caseSensitive,
        int maxResults,
        Func<MemoryEntry, string> documentText,
        TimeSpan perEntryTimeout,
        TimeSpan overallBudget)
    {
        if (pattern.Length > MaxPatternLength)
            throw new MemorySearchException(
                $"Regex pattern exceeds {MaxPatternLength} characters. Narrow the pattern.");

        Regex regex;
        var options = RegexOptions.CultureInvariant;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;
        try
        {
            regex = new Regex(pattern, options, perEntryTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new MemorySearchException($"Invalid regex pattern: {ex.Message}", ex);
        }

        var matches = new List<MemoryEntry>();
        var sw = Stopwatch.StartNew();
        var scanned = 0;
        foreach (var entry in candidates)
        {
            if (sw.Elapsed > overallBudget)
                throw new MemorySearchException(
                    $"Regex search exceeded {overallBudget.TotalSeconds:F1}s after scanning " +
                    $"{scanned}/{candidates.Count} entries. Narrow the pattern or add a category filter.");

            scanned++;
            try
            {
                if (regex.IsMatch(documentText(entry)))
                    matches.Add(entry);
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new MemorySearchException(
                    $"Regex match timed out after {perEntryTimeout.TotalSeconds:F1}s on a single entry. " +
                    "Try a more specific pattern.", ex);
            }
        }

        return matches
            .OrderByDescending(e => e.ImportanceScore)
            .ThenByDescending(e => e.LastSeenAt)
            .Take(maxResults)
            .ToList();
    }
}
