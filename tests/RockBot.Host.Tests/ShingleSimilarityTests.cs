namespace RockBot.Host.Tests;

/// <summary>
/// The audit's near-duplicate measure. It must not agree with the vector index it is measuring,
/// so it is checked here purely on text.
/// </summary>
[TestClass]
public class ShingleSimilarityTests
{
    private const int ShingleSize = 3;

    [TestMethod]
    public void IdenticalTextsScoreOne()
    {
        var a = ShingleSimilarity.Shingles("the deploy failed because the image tag was stale", ShingleSize);
        var b = ShingleSimilarity.Shingles("the deploy failed because the image tag was stale", ShingleSize);

        Assert.AreEqual(1.0, ShingleSimilarity.Jaccard(a, b), 1e-9);
    }

    [TestMethod]
    public void CaseAndPunctuationDoNotChangeTheScore()
    {
        var a = ShingleSimilarity.Shingles("Rocky uses timezone America/Chicago.", ShingleSize);
        var b = ShingleSimilarity.Shingles("rocky uses timezone america chicago", ShingleSize);

        Assert.AreEqual(1.0, ShingleSimilarity.Jaccard(a, b), 1e-9);
    }

    [TestMethod]
    public void OverlappingTextsScoreBetweenZeroAndOne()
    {
        var a = ShingleSimilarity.Shingles("the agent stores memories on the shared volume", ShingleSize);
        var b = ShingleSimilarity.Shingles("the agent stores skills on the shared volume", ShingleSize);

        var score = ShingleSimilarity.Jaccard(a, b);

        Assert.IsTrue(score is > 0 and < 1, $"Expected partial overlap, got {score}.");
    }

    [TestMethod]
    public void DisjointTextsScoreZero()
    {
        var a = ShingleSimilarity.Shingles("kubernetes cluster runs in the lakehouse namespace", ShingleSize);
        var b = ShingleSimilarity.Shingles("coffee tastes better ground fresh each morning", ShingleSize);

        Assert.AreEqual(0.0, ShingleSimilarity.Jaccard(a, b), 1e-9);
    }

    [TestMethod]
    public void WordOrderMatters()
    {
        // Same tokens, different meaning. A bag-of-words measure would call these identical.
        var a = ShingleSimilarity.Shingles("the deploy failed because the tag was stale", ShingleSize);
        var b = ShingleSimilarity.Shingles("the tag failed because the deploy was stale", ShingleSize);

        Assert.IsTrue(ShingleSimilarity.Jaccard(a, b) < 1.0);
    }

    [TestMethod]
    public void TextsShorterThanTheShingleSizeProduceNoShingles()
    {
        Assert.AreEqual(0, ShingleSimilarity.Shingles("two words", ShingleSize).Count);
        Assert.AreEqual(0, ShingleSimilarity.Shingles(null, ShingleSize).Count);
        Assert.AreEqual(0, ShingleSimilarity.Shingles("   ", ShingleSize).Count);
    }

    [TestMethod]
    public void ShortEntriesAreSkippedRatherThanMatchingEachOther()
    {
        var entries = new List<MemoryEntry>
        {
            Entry("a", "too short"),
            Entry("b", "also short"),
            Entry("c", "the agent stores memories on the shared volume today"),
            Entry("d", "the agent stores memories on the shared volume today")
        };

        var pairs = ShingleSimilarity.FindNearDuplicatePairs(entries, ShingleSize, 0.3);

        Assert.AreEqual(1, pairs.Count, "Only the two long identical entries should pair.");
        CollectionAssert.AreEquivalent(new[] { "c", "d" }, new[] { pairs[0].IdA, pairs[0].IdB });
    }

    [TestMethod]
    public void PairsBelowTheThresholdAreNotReturned()
    {
        var entries = new List<MemoryEntry>
        {
            Entry("a", "kubernetes cluster runs in the lakehouse namespace on premises"),
            Entry("b", "coffee tastes better when ground fresh each and every morning")
        };

        Assert.AreEqual(0, ShingleSimilarity.FindNearDuplicatePairs(entries, ShingleSize, 0.3).Count);
    }

    private static MemoryEntry Entry(string id, string content) =>
        new(id, content, null, [], DateTimeOffset.UtcNow);
}
