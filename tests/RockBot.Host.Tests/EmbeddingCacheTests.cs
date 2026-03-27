namespace RockBot.Host.Tests;

[TestClass]
public class EmbeddingCacheTests
{
    [TestMethod]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var v = new float[] { 1, 2, 3 };
        var result = EmbeddingCache.CosineSimilarity(v, v);
        Assert.AreEqual(1.0f, result, 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1, 0 };
        var b = new float[] { 0, 1 };
        var result = EmbeddingCache.CosineSimilarity(a, b);
        Assert.AreEqual(0.0f, result, 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_OppositeVectors_ReturnsNegativeOne()
    {
        var a = new float[] { 1, 0 };
        var b = new float[] { -1, 0 };
        var result = EmbeddingCache.CosineSimilarity(a, b);
        Assert.AreEqual(-1.0f, result, 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_ZeroVector_ReturnsZero()
    {
        var a = new float[] { 1, 2, 3 };
        var b = new float[] { 0, 0, 0 };
        var result = EmbeddingCache.CosineSimilarity(a, b);
        Assert.AreEqual(0.0f, result, 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_DifferentLengths_ReturnsZero()
    {
        var a = new float[] { 1, 2 };
        var b = new float[] { 1, 2, 3 };
        var result = EmbeddingCache.CosineSimilarity(a, b);
        Assert.AreEqual(0.0f, result, 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_SimilarVectors_ReturnsHighScore()
    {
        var a = new float[] { 0.9f, 0.1f };
        var b = new float[] { 0.85f, 0.15f };
        var result = EmbeddingCache.CosineSimilarity(a, b);
        Assert.IsTrue(result > 0.99f, $"Expected high similarity, got {result}");
    }

    [TestMethod]
    public void CosineSimilarity_EmptyVectors_ReturnsZero()
    {
        var result = EmbeddingCache.CosineSimilarity([], []);
        Assert.AreEqual(0.0f, result, 0.001f);
    }
}
