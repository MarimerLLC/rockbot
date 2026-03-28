namespace RockBot.Host;

/// <summary>
/// Combines BM25 keyword ranking with vector (cosine similarity) ranking using the
/// csla-mcp normalization pattern: BM25 scores normalized to [0,1] via max-score
/// division, cosine similarity already in [0,1], scores averaged for items found by
/// both methods.
/// </summary>
internal static class HybridRanker
{
    /// <summary>
    /// Returns <paramref name="candidates"/> ranked by a hybrid BM25 + vector score.
    /// Items with zero score in both methods are excluded.
    /// </summary>
    /// <param name="candidates">The full candidate set.</param>
    /// <param name="getDocumentText">Extracts the text used for BM25 tokenization.</param>
    /// <param name="getId">Extracts a unique identifier for consolidation.</param>
    /// <param name="getEmbedding">Returns the cached embedding for a candidate, or null if unavailable.</param>
    /// <param name="queryEmbedding">The embedding of the search query.</param>
    /// <param name="query">The search query string for BM25.</param>
    /// <param name="minSimilarity">Minimum cosine similarity for vector results. Below this, the candidate
    /// is excluded from vector ranking (preventing loosely related content from diluting BM25 results).</param>
    public static IReadOnlyList<T> Rank<T>(
        IReadOnlyList<T> candidates,
        Func<T, string> getDocumentText,
        Func<T, string> getId,
        Func<T, float[]?> getEmbedding,
        float[] queryEmbedding,
        string query,
        float minSimilarity = 0.5f)
    {
        return RankWithScores(candidates, getDocumentText, getId, getEmbedding, queryEmbedding, query, minSimilarity)
            .Select(r => r.Item)
            .ToList();
    }

    /// <summary>
    /// Returns <paramref name="candidates"/> ranked by hybrid score with the combined scores.
    /// </summary>
    public static IReadOnlyList<(T Item, double Score)> RankWithScores<T>(
        IReadOnlyList<T> candidates,
        Func<T, string> getDocumentText,
        Func<T, string> getId,
        Func<T, float[]?> getEmbedding,
        float[] queryEmbedding,
        string query,
        float minSimilarity = 0.5f)
    {
        if (candidates.Count == 0) return [];

        // 1. BM25 scores, normalized to [0, 1]
        var bm25Scores = NormalizeBm25(
            Bm25Ranker.RankWithScores(candidates, getDocumentText, query), getId);

        // 2. Vector scores (cosine similarity, already [0, 1]), filtered by threshold
        var vectorScores = ComputeVectorScores(candidates, getId, getEmbedding, queryEmbedding, minSimilarity);

        // 3. Consolidate
        return Consolidate(candidates, getId, bm25Scores, vectorScores);
    }

    /// <summary>
    /// Normalizes BM25 scores to [0, 1] by dividing by the maximum score.
    /// </summary>
    internal static Dictionary<string, double> NormalizeBm25<T>(
        IReadOnlyList<(T Item, double Score)> ranked,
        Func<T, string>? getId = null)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (ranked.Count == 0) return result;

        var maxScore = ranked[0].Score;
        if (maxScore <= 0) return result;

        foreach (var (item, score) in ranked)
        {
            var id = getId is not null ? getId(item) : item?.ToString() ?? "";
            result[id] = score / maxScore;
        }

        return result;
    }

    private static Dictionary<string, double> ComputeVectorScores<T>(
        IReadOnlyList<T> candidates,
        Func<T, string> getId,
        Func<T, float[]?> getEmbedding,
        float[] queryEmbedding,
        float minSimilarity)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var embedding = getEmbedding(candidate);
            if (embedding is null) continue;

            var similarity = EmbeddingCache.CosineSimilarity(queryEmbedding, embedding);
            if (similarity >= minSimilarity)
                result[getId(candidate)] = similarity;
        }

        return result;
    }

    private static IReadOnlyList<(T Item, double Score)> Consolidate<T>(
        IReadOnlyList<T> candidates,
        Func<T, string> getId,
        Dictionary<string, double> bm25Scores,
        Dictionary<string, double> vectorScores)
    {
        var allIds = new HashSet<string>(bm25Scores.Keys, StringComparer.OrdinalIgnoreCase);
        allIds.UnionWith(vectorScores.Keys);

        if (allIds.Count == 0) return [];

        // Build a lookup from ID to candidate for output
        var idToCandidate = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
            idToCandidate.TryAdd(getId(candidate), candidate);

        var results = new List<(T Item, double Score)>();

        foreach (var id in allIds)
        {
            if (!idToCandidate.TryGetValue(id, out var item))
                continue;

            var hasBm25 = bm25Scores.TryGetValue(id, out var bm25Score);
            var hasVector = vectorScores.TryGetValue(id, out var vectorScore);

            double combinedScore;
            if (hasBm25 && hasVector)
                combinedScore = (bm25Score + vectorScore) / 2.0;
            else if (hasBm25)
                combinedScore = bm25Score;
            else
                combinedScore = vectorScore;

            results.Add((item, combinedScore));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ToList();
    }
}
