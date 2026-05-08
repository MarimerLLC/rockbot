using RockBot.Host;

namespace RockBot.Observation.Tests;

[TestClass]
public class ObservationTargetDefaultsTests
{
    [TestMethod]
    public void Defaults_MatchDesignDocValues()
    {
        // Sanity-check that the design's named defaults match what the type ships
        // with. If these change, design/observation-framework.md must be updated
        // too.
        var target = new ObservationTarget
        {
            Name = "test",
            Filter = new NoopFilter(),
            ExtractionPrompt = "x",
            EvaluationPrompt = "x",
            StateFilePath = "/tmp/x.json",
            OutputMarkdownPath = "/tmp/x.md",
        };

        Assert.AreEqual(ModelTier.Low, target.ExtractionTier);
        Assert.AreEqual(ModelTier.Balanced, target.EvaluationTier);
        Assert.AreEqual(3, target.PromotionThreshold);
        Assert.AreEqual(7, target.CandidateAgingWindowDays);
        Assert.AreEqual(30, target.TheoryAgingWindowDays);
        Assert.AreEqual(0.85f, target.ClusteringSimilarityThreshold);
        Assert.AreEqual(12, target.SnapshotRetentionCount);
        Assert.IsFalse(target.IncludeBehaviorSummary);
    }

    private sealed class NoopFilter : ITranscriptFilter
    {
        public IEnumerable<TranscriptTurn> Filter(IReadOnlyList<TranscriptTurn> turns) => turns;
    }
}
