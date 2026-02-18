using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>
/// Shared Okapi BM25 ranking implementation used by both long-term and working memory stores.
/// </summary>
internal static partial class Bm25Ranker
{
    /// <summary>
    /// Returns <paramref name="candidates"/> ordered by BM25 relevance against <paramref name="query"/>.
    /// Entries with no matching terms (score = 0) are excluded.
    /// </summary>
    /// <remarks>
    /// Uses Okapi BM25 with k1=1.5 and b=0.75 (standard production defaults).
    /// Single-word terms are scored against tokenised document text.
    /// Consecutive two-word query phrases receive 2× weight to reward adjacent term matches.
    /// Document frequencies are precomputed to avoid O(N²) inner loops.
    /// </remarks>
    internal static IReadOnlyList<T> Rank<T>(
        IReadOnlyList<T> candidates,
        Func<T, string> getDocumentText,
        string query,
        double k1 = 1.5, double b = 0.75)
    {
        if (candidates.Count == 0) return [];

        var queryTokens = Tokenize(query)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (queryTokens.Length == 0) return [];

        var queryPhrases = GetTwoWordPhrases(Tokenize(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Build per-document token sets once
        var docs = candidates.Select(e =>
        {
            var text = getDocumentText(e);
            return (Entry: e, Text: text, Tokens: Tokenize(text));
        }).ToArray();

        int N = docs.Length;
        double avgdl = docs.Average(d => (double)d.Tokens.Length);
        if (avgdl == 0) avgdl = 1;

        // Precompute document frequencies (how many docs contain each term/phrase)
        var termDf = queryTokens.ToDictionary(
            t => t,
            t => docs.Count(d => d.Tokens.Any(tok => tok.Equals(t, StringComparison.OrdinalIgnoreCase))),
            StringComparer.OrdinalIgnoreCase);

        var phraseDf = queryPhrases.ToDictionary(
            p => p,
            p => docs.Count(d => d.Text.Contains(p, StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        return docs
            .Select(doc =>
            {
                var score = 0.0;
                int len = doc.Tokens.Length;
                var freq = doc.Tokens
                    .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                // Single-word BM25 scores
                foreach (var term in queryTokens)
                {
                    int n = termDf[term];
                    if (n == 0) continue;

                    int f = freq.GetValueOrDefault(term, 0);
                    if (f == 0) continue;

                    double idf = Math.Log((N - n + 0.5) / (n + 0.5) + 1.0);
                    double tf = f * (k1 + 1) / (f + k1 * (1 - b + b * len / avgdl));
                    score += idf * tf;
                }

                // Two-word phrase bonus — 2× weight for adjacent term matches
                foreach (var phrase in queryPhrases)
                {
                    int n = phraseDf[phrase];
                    if (n == 0) continue;

                    int f = CountPhraseOccurrences(doc.Text, phrase);
                    if (f == 0) continue;

                    double idf = Math.Log((N - n + 0.5) / (n + 0.5) + 1.0);
                    double tf = f * (k1 + 1) / (f + k1 * (1 - b + b * len / avgdl));
                    score += 2.0 * idf * tf;
                }

                return (doc.Entry, Score: score);
            })
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .Select(r => r.Entry)
            .ToList();
    }

    /// <summary>Splits <paramref name="text"/> into lowercase tokens of 3+ characters.</summary>
    internal static string[] Tokenize(string text) =>
        TokenizerPattern().Split(text.ToLowerInvariant())
            .Where(t => t.Length >= 3)
            .ToArray();

    /// <summary>Returns all consecutive two-word phrases from a token array.</summary>
    private static string[] GetTwoWordPhrases(string[] tokens) =>
        tokens.Length < 2
            ? []
            : Enumerable.Range(0, tokens.Length - 1)
                .Select(i => $"{tokens[i]} {tokens[i + 1]}")
                .ToArray();

    /// <summary>Counts case-insensitive non-overlapping occurrences of <paramref name="phrase"/> in <paramref name="text"/>.</summary>
    private static int CountPhraseOccurrences(string text, string phrase)
    {
        int count = 0, start = 0;
        while (true)
        {
            int idx = text.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            count++;
            start = idx + phrase.Length;
        }
        return count;
    }

    /// <summary>Splits on any sequence of characters that are not lowercase letters or digits.</summary>
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex TokenizerPattern();
}
