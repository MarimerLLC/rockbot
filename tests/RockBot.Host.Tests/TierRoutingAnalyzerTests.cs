using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public class TierRoutingAnalyzerTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 22, 10, 0, 0, TimeSpan.Zero);

    private static TierRoutingEntry MakeEntry(
        ModelTier tier = ModelTier.Balanced,
        double score = 0.30,
        int? toolCalls = 1,
        long? inputTokens = 1000,
        long? outputTokens = 500,
        int? postInjectionTokens = 5000,
        string promptPreview = "do a thing",
        string[]? highKeywords = null,
        string[]? lowKeywords = null,
        bool fallback = false,
        string context = "user-message",
        string? modelId = "claude-sonnet-4-6",
        int timestampOffsetMinutes = 0)
    {
        return new TierRoutingEntry
        {
            Timestamp = BaseTime.AddMinutes(timestampOffsetMinutes),
            PromptPreview = promptPreview,
            Tier = tier,
            Context = context,
            ComplexityScore = score,
            MatchedHighKeywords = highKeywords ?? [],
            MatchedLowKeywords = lowKeywords ?? [],
            PostInjectionTokenEstimate = postInjectionTokens,
            ModelId = modelId,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ToolCallCount = toolCalls,
            IsFallbackTriggered = fallback,
        };
    }

    // ── Empty / minimal input ──────────────────────────────────────────────────

    [TestMethod]
    public void Analyze_EmptyEntries_ReturnsZeroedAnalysis()
    {
        var result = TierRoutingAnalyzer.Analyze([]);
        Assert.AreEqual(0, result.TotalEntries);
        Assert.AreEqual(0, result.Clusters.Count);
        Assert.AreEqual(0, result.FlaggedClusters.Count);
        Assert.AreEqual(0, result.ThresholdScans.Count);
        Assert.AreEqual(TierRoutingAnalyzer.CurrentSchemaVersion, result.SchemaVersion);
    }

    // ── Detection rules ────────────────────────────────────────────────────────

    [TestMethod]
    public void Analyze_LowTierWithManyToolCalls_FlagsPanicEscalation()
    {
        var entries = Enumerable.Range(0, 4)
            .Select(_ => MakeEntry(tier: ModelTier.Low, toolCalls: 4, promptPreview: "fix the bug"))
            .ToList();

        var result = TierRoutingAnalyzer.Analyze(entries);
        Assert.AreEqual(1, result.FlaggedClusters.Count);
        Assert.AreEqual("panicEscalation", result.FlaggedClusters[0].Flag);
        Assert.AreEqual(ModelTier.Balanced, result.FlaggedClusters[0].AlternateTier);
    }

    [TestMethod]
    public void Analyze_LowToolCallCount_DoesNotFlagPanic()
    {
        var entries = Enumerable.Range(0, 5)
            .Select(_ => MakeEntry(tier: ModelTier.Low, toolCalls: 1))
            .ToList();

        var result = TierRoutingAnalyzer.Analyze(entries);
        Assert.AreEqual(0, result.FlaggedClusters.Count);
    }

    [TestMethod]
    public void Analyze_SmallCluster_DoesNotFlagEvenWithMatchingPattern()
    {
        // Only 2 entries — below MinClusterSize=3; should not flag.
        var entries = Enumerable.Range(0, 2)
            .Select(_ => MakeEntry(tier: ModelTier.Low, toolCalls: 5))
            .ToList();

        var result = TierRoutingAnalyzer.Analyze(entries);
        Assert.AreEqual(0, result.FlaggedClusters.Count);
    }

    [TestMethod]
    public void Analyze_LowScoreHighPostInjection_FlagsTokenSurprise()
    {
        var entries = Enumerable.Range(0, 3)
            .Select(_ => MakeEntry(score: 0.10, postInjectionTokens: 15000, promptPreview: "what time is it"))
            .ToList();

        var result = TierRoutingAnalyzer.Analyze(entries);
        Assert.AreEqual(1, result.FlaggedClusters.Count);
        Assert.AreEqual("tokenSurprise", result.FlaggedClusters[0].Flag);
        Assert.IsNull(result.FlaggedClusters[0].AlternateTier);
    }

    [TestMethod]
    public void Analyze_HighTierLowOutput_FlagsOverRouting()
    {
        var entries = Enumerable.Range(0, 3)
            .Select(_ => MakeEntry(
                tier: ModelTier.High, score: 0.80, toolCalls: 1,
                outputTokens: 50, promptPreview: "analyze this complex topic"))
            .ToList();

        var result = TierRoutingAnalyzer.Analyze(entries);
        Assert.AreEqual(1, result.FlaggedClusters.Count);
        Assert.AreEqual("lowOutputAtHigh", result.FlaggedClusters[0].Flag);
        Assert.AreEqual(ModelTier.Balanced, result.FlaggedClusters[0].AlternateTier);
    }

    [TestMethod]
    public void Analyze_FallbackEntries_ExcludedFromFlagging()
    {
        // Fallback entries should NOT contribute to flagged-cluster detection,
        // but DO appear in global stats and FallbackExcludedCount.
        var entries = Enumerable.Range(0, 5)
            .Select(_ => MakeEntry(tier: ModelTier.Low, toolCalls: 5, fallback: true))
            .ToList();

        var result = TierRoutingAnalyzer.Analyze(entries);
        Assert.AreEqual(5, result.TotalEntries);
        Assert.AreEqual(5, result.FallbackExcludedCount);
        Assert.AreEqual(0, result.FlaggedClusters.Count);
    }

    // ── Clustering ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void BuildClusters_SameKeywordsAndTier_GroupTogether()
    {
        var entries = new[]
        {
            MakeEntry(highKeywords: ["analyze"], toolCalls: 1),
            MakeEntry(highKeywords: ["analyze"], toolCalls: 1, promptPreview: "different prompt text"),
            MakeEntry(highKeywords: ["compare"], toolCalls: 1),
        };

        var result = TierRoutingAnalyzer.Analyze(entries);
        // Two clusters: {"analyze"} count=2, {"compare"} count=1
        Assert.AreEqual(2, result.Clusters.Count);
        Assert.AreEqual(2, result.Clusters[0].Count);  // ordered by count desc
        Assert.AreEqual(1, result.Clusters[1].Count);
    }

    [TestMethod]
    public void BuildClusters_NoKeywords_UsesFirstThreeWordsFallback()
    {
        // No matched keywords — clustering must fall back to first 3 words to avoid
        // a giant catch-all bucket.
        var entries = new[]
        {
            MakeEntry(promptPreview: "check my calendar today please"),
            MakeEntry(promptPreview: "check my calendar tomorrow if free"),
            MakeEntry(promptPreview: "send an email to bob"),
        };

        var result = TierRoutingAnalyzer.Analyze(entries);
        // First two share "check my calendar" prefix → 1 cluster; third → separate
        Assert.AreEqual(2, result.Clusters.Count);
        Assert.AreEqual(2, result.Clusters[0].Count);
    }

    [TestMethod]
    public void BuildClusters_DifferentToolCallBuckets_StaySeparate()
    {
        var entries = new[]
        {
            MakeEntry(highKeywords: ["analyze"], toolCalls: 0),
            MakeEntry(highKeywords: ["analyze"], toolCalls: 4),
        };

        var result = TierRoutingAnalyzer.Analyze(entries);
        Assert.AreEqual(2, result.Clusters.Count);
    }

    // ── Keyword candidates ─────────────────────────────────────────────────────

    [TestMethod]
    public void BuildKeywordCandidates_HighTierBiasedWord_SurfacesAsHighSignal()
    {
        var entries = new List<TierRoutingEntry>();
        // 6 High-tier prompts containing "refactor", 1 Low-tier prompt containing "refactor"
        for (var i = 0; i < 6; i++)
            entries.Add(MakeEntry(tier: ModelTier.High, score: 0.80, promptPreview: "please refactor this module"));
        entries.Add(MakeEntry(tier: ModelTier.Low, score: 0.10, promptPreview: "what time refactor"));

        var result = TierRoutingAnalyzer.Analyze(entries);

        var refactor = result.KeywordCandidates.HighSignalCandidates
            .FirstOrDefault(c => c.Keyword == "refactor");
        Assert.IsNotNull(refactor);
        Assert.AreEqual(6, refactor.HighTierCount);
    }

    [TestMethod]
    public void BuildKeywordCandidates_AlreadyMatchedKeyword_Excluded()
    {
        // If a word already shows up in MatchedHighKeywords, it should not be re-surfaced
        // as a candidate (we want NEW candidates, not the ones already in use).
        var entries = Enumerable.Range(0, 6)
            .Select(_ => MakeEntry(
                tier: ModelTier.High, score: 0.80,
                promptPreview: "please analyze this complex topic",
                highKeywords: ["analyze"]))
            .ToList();

        var result = TierRoutingAnalyzer.Analyze(entries);
        var analyzeCandidate = result.KeywordCandidates.HighSignalCandidates
            .FirstOrDefault(c => c.Keyword == "analyze");
        Assert.IsNull(analyzeCandidate, "'analyze' is already a matched keyword; should not be re-surfaced");
    }

    // ── Threshold scans ────────────────────────────────────────────────────────

    [TestMethod]
    public void BuildThresholdScans_ShiftFlipsEntriesNearBoundary()
    {
        // With defaults: lowCeiling=0.15, balancedCeiling=0.46.
        // An entry with score 0.18 simulates Balanced at original, Low at lowCeiling+0.05 (=0.20).
        var entries = new[]
        {
            MakeEntry(tier: ModelTier.Balanced, score: 0.18),
            MakeEntry(tier: ModelTier.Balanced, score: 0.19),
            MakeEntry(tier: ModelTier.Low, score: 0.10),  // unaffected
        };

        var result = TierRoutingAnalyzer.Analyze(entries);
        var lowCeilingUp = result.ThresholdScans.First(s => s.Threshold == "lowCeiling" && s.Delta > 0);
        Assert.AreEqual(2, lowCeilingUp.EntriesFlipped);
        StringAssert.Contains(lowCeilingUp.DirectionDescription, "Balanced->Low");
    }

    [TestMethod]
    public void BuildThresholdScans_AtExactBoundary_NoFloatingPointFlips()
    {
        // Regression: 0.15 - 0.05 yields 0.09999999999999998 as a double, which made
        // entries with score 0.10 falsely appear to flip when lowCeiling shifted by -0.05.
        // After rounding the shifted threshold to 4 decimals, score 0.10 entries should
        // remain Low (0.10 <= 0.10) and only entries strictly above the new ceiling flip.
        var entries = new[]
        {
            MakeEntry(tier: ModelTier.Low, score: 0.10),  // at the new boundary — should NOT flip
            MakeEntry(tier: ModelTier.Low, score: 0.10),  // at the new boundary — should NOT flip
            MakeEntry(tier: ModelTier.Low, score: 0.11),  // above the new boundary — should flip
        };

        var result = TierRoutingAnalyzer.Analyze(entries);
        var lowCeilingDown = result.ThresholdScans.First(s => s.Threshold == "lowCeiling" && s.Delta < 0);
        Assert.AreEqual(1, lowCeilingDown.EntriesFlipped, "Only the score=0.11 entry should flip after rounding the shifted threshold");
    }

    [TestMethod]
    public void BuildThresholdScans_NoCostDelta_WhenPricingMissing()
    {
        var entries = new[]
        {
            MakeEntry(tier: ModelTier.Balanced, score: 0.18),
        };

        var result = TierRoutingAnalyzer.Analyze(entries);
        Assert.IsTrue(result.ThresholdScans.All(s => s.ProjectedCostDelta is null));
    }

    // ── Projected cost ─────────────────────────────────────────────────────────

    [TestMethod]
    public void ProjectedCost_WithPricingAndModelId_ComputesPerCall()
    {
        var pricing = new[]
        {
            new LlmPricingRow("claude-sonnet-4", 3.00m, 15.00m),
        };

        var entries = new[]
        {
            MakeEntry(inputTokens: 1_000_000, outputTokens: 100_000, modelId: "claude-sonnet-4-6"),
        };

        var result = TierRoutingAnalyzer.Analyze(entries, pricing: pricing);
        // 1M input × $3/M + 100k output × $15/M = $3.00 + $1.50 = $4.50
        Assert.AreEqual(4.50m, result.ProjectedCost.TotalUsd);
        Assert.AreEqual(1, result.ProjectedCost.EntriesPricedCount);
        Assert.AreEqual(0, result.ProjectedCost.EntriesUnpricedCount);
    }

    [TestMethod]
    public void ProjectedCost_UnknownModelId_CountsAsUnpriced()
    {
        var pricing = new[]
        {
            new LlmPricingRow("claude-sonnet-4", 3.00m, 15.00m),
        };

        var entries = new[]
        {
            MakeEntry(inputTokens: 1000, outputTokens: 500, modelId: "totally-unknown-model"),
            MakeEntry(inputTokens: 1000, outputTokens: 500, modelId: null),
        };

        var result = TierRoutingAnalyzer.Analyze(entries, pricing: pricing);
        Assert.AreEqual(0m, result.ProjectedCost.TotalUsd);
        Assert.AreEqual(0, result.ProjectedCost.EntriesPricedCount);
        Assert.AreEqual(2, result.ProjectedCost.EntriesUnpricedCount);
    }

    // ── Global stats ───────────────────────────────────────────────────────────

    [TestMethod]
    public void GlobalStats_TierPercentages_SumToHundred()
    {
        var entries = new[]
        {
            MakeEntry(tier: ModelTier.Low),
            MakeEntry(tier: ModelTier.Balanced),
            MakeEntry(tier: ModelTier.Balanced),
            MakeEntry(tier: ModelTier.High),
        };

        var result = TierRoutingAnalyzer.Analyze(entries);
        var sum = result.GlobalStats.ByTier.Sum(t => t.Pct);
        Assert.AreEqual(100.0, sum, 0.1);
    }

    [TestMethod]
    public void GlobalStats_FallbackPctIncludesAllEntries()
    {
        var entries = new[]
        {
            MakeEntry(fallback: true),
            MakeEntry(fallback: true),
            MakeEntry(fallback: false),
            MakeEntry(fallback: false),
        };

        var result = TierRoutingAnalyzer.Analyze(entries);
        Assert.AreEqual(50.0, result.GlobalStats.FallbackPct);
        Assert.AreEqual(2, result.GlobalStats.FallbackCount);
    }

    // ── Routing cost floor (High-tier over-routing guard) ──────────────────────

    private static IReadOnlyDictionary<ModelTier, string?> ModelMap(string low, string balanced, string high) =>
        new Dictionary<ModelTier, string?>
        {
            [ModelTier.Low] = low,
            [ModelTier.Balanced] = balanced,
            [ModelTier.High] = high,
        };

    [TestMethod]
    public void CostFloor_SameModelHighAndBalanced_MakesShiftCostNonZeroWithCorrectSign()
    {
        // High and Balanced share a model, so real pricing shows a Balanced→High shift as
        // zero-cost. The 2× floor must surface it as a POSITIVE (unfavorable) delta on the
        // balancedCeiling-DOWN scan and a NEGATIVE (favorable) delta on the UP scan — the
        // signal that stops the floor-ward ratchet and lets the dream climb back up.
        var pricing = new[]
        {
            new LlmPricingRow("gpt-5.5", 5.00m, 30.00m),
            new LlmPricingRow("mini", 0.75m, 4.50m),
        };
        var map = ModelMap(low: "mini", balanced: "gpt-5.5", high: "gpt-5.5");

        var entries = new List<TierRoutingEntry>();
        // scores in (0.41, 0.46] flip Balanced→High on the -0.05 scan
        for (var i = 0; i < 3; i++)
            entries.Add(MakeEntry(tier: ModelTier.Balanced, score: 0.44, promptPreview: $"balanced boundary {i}"));
        // scores in (0.46, 0.51] flip High→Balanced on the +0.05 scan
        for (var i = 0; i < 3; i++)
            entries.Add(MakeEntry(tier: ModelTier.High, score: 0.49, promptPreview: $"high boundary {i}"));

        var result = TierRoutingAnalyzer.Analyze(
            entries, pricing: pricing, tierModelMap: map, highTierCostFloorMultiplier: 2.0);

        var balDown = result.ThresholdScans.First(s => s.Threshold == "balancedCeiling" && s.Delta < 0);
        var balUp = result.ThresholdScans.First(s => s.Threshold == "balancedCeiling" && s.Delta > 0);

        Assert.IsNotNull(balDown.ProjectedCostDelta);
        Assert.IsTrue(balDown.ProjectedCostDelta > 0m,
            $"Balanced→High shift should project a positive (unfavorable) cost delta, was {balDown.ProjectedCostDelta}");
        Assert.IsNotNull(balUp.ProjectedCostDelta);
        Assert.IsTrue(balUp.ProjectedCostDelta < 0m,
            $"High→Balanced shift should project a negative (favorable) cost delta, was {balUp.ProjectedCostDelta}");
    }

    [TestMethod]
    public void CostFloor_Disabled_SameModelShiftProjectsZeroCost()
    {
        // Floor disabled (multiplier 1.0) + shared model → cost delta collapses to zero. This
        // is exactly the "free ratchet" the floor exists to fix; pinned here as a regression.
        var pricing = new[] { new LlmPricingRow("gpt-5.5", 5.00m, 30.00m) };
        var map = ModelMap(low: "gpt-5.5", balanced: "gpt-5.5", high: "gpt-5.5");

        var entries = Enumerable.Range(0, 3)
            .Select(i => MakeEntry(tier: ModelTier.Balanced, score: 0.44, promptPreview: $"boundary {i}"))
            .ToList();

        var result = TierRoutingAnalyzer.Analyze(
            entries, pricing: pricing, tierModelMap: map, highTierCostFloorMultiplier: 1.0);

        var balDown = result.ThresholdScans.First(s => s.Threshold == "balancedCeiling" && s.Delta < 0);
        Assert.AreEqual(0m, balDown.ProjectedCostDelta);
    }

    [TestMethod]
    public void CostFloor_PremiumHighModel_UsesRealPriceNotDiscountedFloor()
    {
        // When High genuinely uses a model more expensive than 2× Balanced, Math.Max keeps the
        // real (higher) price — the floor must never DISCOUNT a premium tier.
        var pricing = new[]
        {
            new LlmPricingRow("gpt-5.5-pro", 30.00m, 180.00m),
            new LlmPricingRow("gpt-5.5", 5.00m, 30.00m),
        };
        var map = ModelMap(low: "gpt-5.5", balanced: "gpt-5.5", high: "gpt-5.5-pro");

        var entries = Enumerable.Range(0, 3)
            .Select(i => MakeEntry(tier: ModelTier.Balanced, score: 0.44,
                inputTokens: 1000, outputTokens: 500, promptPreview: $"boundary {i}"))
            .ToList();

        var result = TierRoutingAnalyzer.Analyze(
            entries, pricing: pricing, tierModelMap: map, highTierCostFloorMultiplier: 2.0);

        // Per call: High(pro)=(1000×30 + 500×180)/1e6 = 0.12 ; Balanced=(1000×5 + 500×30)/1e6 = 0.02.
        // 3 entries flip Balanced→High on the -0.05 scan → delta = 3 × (0.12 - 0.02) = 0.30.
        var balDown = result.ThresholdScans.First(s => s.Threshold == "balancedCeiling" && s.Delta < 0);
        Assert.AreEqual(0.30m, balDown.ProjectedCostDelta);
    }
}
