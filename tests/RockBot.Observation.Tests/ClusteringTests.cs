namespace RockBot.Observation.Tests;

[TestClass]
public class ClusteringTests
{
    [TestMethod]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        float[] v = [1, 0, 0];
        Assert.AreEqual(1f, Clustering.CosineSimilarity(v, v), 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        float[] a = [1, 0, 0];
        float[] b = [0, 1, 0];
        Assert.AreEqual(0f, Clustering.CosineSimilarity(a, b), 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_OppositeVectors_ReturnsNegativeOne()
    {
        float[] a = [1, 0, 0];
        float[] b = [-1, 0, 0];
        Assert.AreEqual(-1f, Clustering.CosineSimilarity(a, b), 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_EmptyVector_ReturnsZero()
    {
        Assert.AreEqual(0f, Clustering.CosineSimilarity([], []));
    }

    [TestMethod]
    public void CosineSimilarity_LengthMismatch_ReturnsZero()
    {
        float[] a = [1, 2, 3];
        float[] b = [1, 2];
        Assert.AreEqual(0f, Clustering.CosineSimilarity(a, b));
    }

    [TestMethod]
    public void CosineSimilarity_ZeroMagnitude_ReturnsZero()
    {
        float[] a = [0, 0, 0];
        float[] b = [1, 0, 0];
        Assert.AreEqual(0f, Clustering.CosineSimilarity(a, b));
    }

    [TestMethod]
    public void FindBestMatch_AboveThreshold_ReturnsCandidate()
    {
        var c1 = MakeCandidate("c1", [1, 0, 0]);
        var c2 = MakeCandidate("c2", [0, 1, 0]);

        var result = Clustering.FindBestMatch(
            [0.99f, 0.01f, 0],
            [c1, c2],
            threshold: 0.85f);

        Assert.IsNotNull(result);
        Assert.AreEqual("c1", result.Value.Candidate.Id);
    }

    [TestMethod]
    public void FindBestMatch_BelowThreshold_ReturnsNull()
    {
        var c1 = MakeCandidate("c1", [1, 0, 0]);
        var c2 = MakeCandidate("c2", [0, 1, 0]);

        var result = Clustering.FindBestMatch(
            [0.5f, 0.5f, 0.7071f],
            [c1, c2],
            threshold: 0.85f);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void FindBestMatch_PicksBestEvenIfMultipleAboveThreshold()
    {
        var c1 = MakeCandidate("c1", [1, 0, 0]);
        var c2 = MakeCandidate("c2", [0.9f, 0.1f, 0]);

        // Query is closer to c2.
        var result = Clustering.FindBestMatch(
            [0.9f, 0.1f, 0],
            [c1, c2],
            threshold: 0.7f);

        Assert.IsNotNull(result);
        Assert.AreEqual("c2", result.Value.Candidate.Id);
    }

    [TestMethod]
    public void FindBestMatch_EmptyCandidates_ReturnsNull()
    {
        var result = Clustering.FindBestMatch(
            [1, 0, 0],
            [],
            threshold: 0.5f);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void FindBestMatch_CandidateWithNullVector_Skipped()
    {
        var c1 = MakeCandidate("c1", null!);
        var c2 = MakeCandidate("c2", [1, 0, 0]);

        var result = Clustering.FindBestMatch(
            [1, 0, 0],
            [c1, c2],
            threshold: 0.5f);

        Assert.IsNotNull(result);
        Assert.AreEqual("c2", result.Value.Candidate.Id,
            "Candidates without vectors should be skipped, not crash");
    }

    private static Candidate MakeCandidate(string id, float[] vector) => new()
    {
        Id = id,
        Text = $"text {id}",
        ClusterId = $"clust {id}",
        Count = 0,
        FirstSeen = DateTimeOffset.UtcNow,
        LastSeen = DateTimeOffset.UtcNow,
        Vector = vector,
    };
}
