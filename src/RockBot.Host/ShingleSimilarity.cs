namespace RockBot.Host;

/// <summary>
/// Lexical near-duplicate detection over word n-gram shingles, scored by Jaccard overlap.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately independent of embeddings. The audit has to produce the same number on a
/// BM25-only deployment as on a hybrid one, and it must not agree with the mechanism it is
/// measuring: if consolidation and the audit both asked the vector index "are these the same?",
/// a broken index would read as a clean corpus.
/// </para>
/// <para>
/// Shingles rather than bag-of-words because word order carries the distinction that matters
/// here — "the deploy failed because the tag was stale" and "the tag failed because the deploy
/// was stale" share every token and say different things.
/// </para>
/// </remarks>
internal static class ShingleSimilarity
{
    /// <summary>A pair of live entries whose texts overlap above the threshold.</summary>
    internal sealed record Pair(string IdA, string IdB, double Score);

    /// <summary>
    /// Returns every pair of <paramref name="entries"/> scoring at or above
    /// <paramref name="threshold"/>. Entries with fewer than <paramref name="shingleSize"/>
    /// words produce no shingles and are skipped — a four-word fact has no n-grams to compare,
    /// and padding it would make every short entry look like every other one.
    /// </summary>
    internal static IReadOnlyList<Pair> FindNearDuplicatePairs(
        IReadOnlyList<MemoryEntry> entries,
        int shingleSize,
        double threshold,
        CancellationToken ct = default)
    {
        if (shingleSize < 1) shingleSize = 1;

        var ids = new List<string>(entries.Count);
        var sets = new List<HashSet<int>>(entries.Count);

        foreach (var entry in entries)
        {
            var set = Shingles(entry.Content, shingleSize);
            if (set.Count == 0) continue;
            ids.Add(entry.Id);
            sets.Add(set);
        }

        var pairs = new List<Pair>();

        for (var i = 0; i < sets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (var j = i + 1; j < sets.Count; j++)
            {
                var score = Jaccard(sets[i], sets[j]);
                if (score >= threshold)
                    pairs.Add(new Pair(ids[i], ids[j], score));
            }
        }

        return pairs;
    }

    /// <summary>
    /// Hashes of the word n-grams in <paramref name="text"/>. Hashes rather than strings because
    /// a thousand-entry corpus produces on the order of 10^5 shingles and only equality is ever
    /// asked of them.
    /// </summary>
    internal static HashSet<int> Shingles(string? text, int shingleSize)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(text)) return set;

        var words = Normalize(text);
        if (words.Length < shingleSize) return set;

        for (var i = 0; i + shingleSize <= words.Length; i++)
        {
            var hash = new HashCode();
            for (var k = 0; k < shingleSize; k++)
                hash.Add(words[i + k], StringComparer.Ordinal);
            set.Add(hash.ToHashCode());
        }

        return set;
    }

    /// <summary>Intersection over union. Two empty sets score zero, not one.</summary>
    internal static double Jaccard(HashSet<int> a, HashSet<int> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;

        // Iterate the smaller set; the larger one answers Contains in O(1) either way.
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);

        var intersection = 0;
        foreach (var shingle in small)
            if (large.Contains(shingle))
                intersection++;

        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>
    /// Lowercased word tokens, punctuation dropped. Case and punctuation are exactly the
    /// differences a rephrasing introduces without changing what the entry says.
    /// </summary>
    private static string[] Normalize(string text)
    {
        var buffer = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                buffer.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            buffer.Add(current.ToString());

        return [.. buffer];
    }
}
