namespace RockBot.Observation;

/// <summary>
/// Vector-space helpers for matching proposed observations to existing
/// candidate clusters. Cosine similarity is the only metric used; embeddings
/// are produced by the host's <c>IEmbeddingGenerator</c> and stored on
/// <see cref="Candidate.Vector"/>.
/// </summary>
internal static class Clustering
{
    /// <summary>
    /// Returns the candidate whose vector has the highest cosine similarity
    /// to <paramref name="vector"/>, provided that similarity meets or
    /// exceeds <paramref name="threshold"/>. Returns null if no candidate is
    /// above threshold (or if the candidate pool has no candidates with
    /// vectors).
    /// </summary>
    public static (Candidate Candidate, float Similarity)? FindBestMatch(
        ReadOnlySpan<float> vector,
        IReadOnlyList<Candidate> candidates,
        float threshold)
    {
        Candidate? best = null;
        var bestSim = -1f;

        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (c.Vector is null || c.Vector.Length == 0)
                continue;

            var sim = CosineSimilarity(vector, c.Vector);
            if (sim > bestSim)
            {
                bestSim = sim;
                best = c;
            }
        }

        if (best is null || bestSim < threshold)
            return null;

        return (best, bestSim);
    }

    /// <summary>
    /// Cosine similarity for two equal-length vectors. Returns 0 if either
    /// vector is zero-length or has zero magnitude (avoids NaN propagation
    /// into the merge logic).
    /// </summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length)
            return 0f;

        double dot = 0;
        double aMag = 0;
        double bMag = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            aMag += a[i] * a[i];
            bMag += b[i] * b[i];
        }

        if (aMag == 0 || bMag == 0)
            return 0f;

        return (float)(dot / (Math.Sqrt(aMag) * Math.Sqrt(bMag)));
    }
}
