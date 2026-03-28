namespace RockBot.Host.Tests;

[TestClass]
public class HybridRankerTests
{
    [TestMethod]
    public void NormalizeBm25_DividesByMaxScore()
    {
        var ranked = new List<(string Item, double Score)>
        {
            ("a", 10.0),
            ("b", 5.0),
            ("c", 2.5)
        };

        var normalized = HybridRanker.NormalizeBm25(ranked, static s => s);

        Assert.AreEqual(1.0, normalized["a"], 0.001);
        Assert.AreEqual(0.5, normalized["b"], 0.001);
        Assert.AreEqual(0.25, normalized["c"], 0.001);
    }

    [TestMethod]
    public void NormalizeBm25_EmptyInput_ReturnsEmpty()
    {
        var ranked = new List<(string Item, double Score)>();

        var normalized = HybridRanker.NormalizeBm25(ranked, static s => s);

        Assert.AreEqual(0, normalized.Count);
    }

    [TestMethod]
    public void NormalizeBm25_ZeroMaxScore_ReturnsEmpty()
    {
        var ranked = new List<(string Item, double Score)> { ("a", 0.0) };

        var normalized = HybridRanker.NormalizeBm25(ranked, static s => s);

        Assert.AreEqual(0, normalized.Count);
    }

    [TestMethod]
    public void Rank_BothMethodsContributeToSameItem_ScoresAreAveraged()
    {
        var candidates = new[]
        {
            new TestDoc("doc1", "the quick brown fox", new float[] { 0.9f, 0.1f }),
            new TestDoc("doc2", "lazy dog sleeps all day", new float[] { 0.1f, 0.9f })
        };

        // Query embedding is close to doc1
        var queryEmbedding = new float[] { 0.85f, 0.15f };

        var results = HybridRanker.RankWithScores(
            candidates,
            static d => d.Text,
            static d => d.Id,
            d => d.Embedding,
            queryEmbedding,
            "quick brown fox");

        // doc1 should rank higher — it matches both BM25 (exact terms) and vector (close embedding)
        Assert.IsTrue(results.Count > 0);
        Assert.AreEqual("doc1", results[0].Item.Id);
    }

    [TestMethod]
    public void Rank_ItemOnlyInBm25_UsesWordScoreAlone()
    {
        var candidates = new[]
        {
            new TestDoc("doc1", "the quick brown fox", null),  // no embedding
            new TestDoc("doc2", "lazy dog sleeps all day", null)
        };

        var queryEmbedding = new float[] { 1.0f };

        var results = HybridRanker.RankWithScores(
            candidates,
            static d => d.Text,
            static d => d.Id,
            static d => d.Embedding,
            queryEmbedding,
            "quick brown fox");

        // doc1 matches BM25 (has the query terms), doc2 does not
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("doc1", results[0].Item.Id);
    }

    [TestMethod]
    public void Rank_ItemOnlyInVector_UsesVectorScoreAlone()
    {
        var candidates = new[]
        {
            new TestDoc("doc1", "completely unrelated text", new float[] { 0.95f, 0.05f }),
            new TestDoc("doc2", "also unrelated content here", new float[] { 0.05f, 0.95f })
        };

        // Query embedding is close to doc1, but query text matches neither
        var queryEmbedding = new float[] { 0.9f, 0.1f };

        var results = HybridRanker.RankWithScores(
            candidates,
            static d => d.Text,
            static d => d.Id,
            d => d.Embedding,
            queryEmbedding,
            "zebra giraffe elephant");

        // doc1 should rank higher via vector similarity alone
        Assert.IsTrue(results.Count > 0);
        Assert.AreEqual("doc1", results[0].Item.Id);
    }

    [TestMethod]
    public void Rank_EmptyCandidates_ReturnsEmpty()
    {
        var results = HybridRanker.Rank(
            Array.Empty<TestDoc>(),
            static d => d.Text,
            static d => d.Id,
            static d => d.Embedding,
            new float[] { 1.0f },
            "test");

        Assert.AreEqual(0, results.Count);
    }

    private sealed record TestDoc(string Id, string Text, float[]? Embedding);
}
