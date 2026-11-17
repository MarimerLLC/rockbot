using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class LlmCostEstimatorTests
{
    private static LlmCostEstimator NewEstimator(string configPath)
    {
        var opts = Options.Create(new LlmPricingOptions { ConfigPath = configPath });
        return new LlmCostEstimator(opts, NullLogger<LlmCostEstimator>.Instance);
    }

    [TestMethod]
    public void EstimateCost_UsesBuiltInDefaults_WhenFileMissing()
    {
        using var estimator = NewEstimator(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        // gpt-5.4 is in built-in defaults at $2.50/M input, $15.00/M output.
        var cost = estimator.EstimateCost("gpt-5.4", 1_000_000, 1_000_000);

        Assert.AreEqual(17.50, cost, 0.0001);
    }

    [TestMethod]
    public void EstimateCost_LongestPrefixWins()
    {
        // gpt-5.4-pro must match before gpt-5.4 even though both contain "gpt-5.4".
        using var estimator = NewEstimator(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        var cost = estimator.EstimateCost("gpt-5.4-pro", 1_000_000, 0);

        Assert.AreEqual(30.00, cost, 0.0001);
    }

    [TestMethod]
    [DataRow("gpt-5.6-terra", 14.00)]
    [DataRow("gpt-5.6-terra-2026-07-09", 14.00)]
    [DataRow("gpt-5.6-sol", 35.00)]
    [DataRow("gpt-5.6-luna", 1.40)]
    [DataRow("gpt-5.5", 35.00)]
    public void EstimateCost_BuiltInDefaults_CoverDeployedGpt5Models(string modelId, double expected)
    {
        using var estimator = NewEstimator(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.AreEqual(expected, estimator.EstimateCost(modelId, 1_000_000, 1_000_000), 0.0001);
    }

    [TestMethod]
    public void EstimateCost_ReturnsZero_ForUnknownModel()
    {
        using var estimator = NewEstimator(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.AreEqual(0.0, estimator.EstimateCost("totally-unknown-model", 1_000_000, 1_000_000));
    }

    [TestMethod]
    public void EstimateCost_LoadsFromFile_OverridingDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pricing-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            [
              { "prefix": "my-model", "inputPerM": 100.0, "outputPerM": 200.0 }
            ]
            """);
        try
        {
            using var estimator = NewEstimator(path);

            // From file: my-model = $100/M input, $200/M output.
            Assert.AreEqual(0.30, estimator.EstimateCost("my-model-v1", 1_000, 1_000), 0.0001);

            // gpt-5.4 was in built-in defaults but the file fully replaces them, so it returns 0.
            Assert.AreEqual(0.0, estimator.EstimateCost("gpt-5.4", 1_000_000, 1_000_000));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static LlmCostEstimator BuiltIn() =>
        NewEstimator(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

    [TestMethod]
    [DataRow("claude-sonnet-5-5", 12.00)]
    [DataRow("claude-opus-5-5", 24.00)]
    [DataRow("claude-haiku-5-5", 0.60)]
    [DataRow("claude-fable-5-1", 60.00)]
    public void EstimateCost_BuiltInDefaults_CoverClaude5Models(string modelId, double expected)
    {
        using var estimator = BuiltIn();

        Assert.AreEqual(expected, estimator.EstimateCost(modelId, 1_000_000, 1_000_000), 0.0001);
    }

    [TestMethod]
    public void EstimateCost_PricesCacheReadsAndWrites()
    {
        using var estimator = BuiltIn();

        // Sonnet 5.5: $2/M input, $0.20/M cache read, 5m write 1.25x = $2.50/M, 1h write 2x = $4/M.
        // 4M total input = 1M uncached + 1M read + 1M 5m-write + 1M 1h-write.
        var cost = estimator.EstimateCost("claude-sonnet-5-5",
            inputTokens: 4_000_000, outputTokens: 0,
            cachedInputTokens: 1_000_000, cacheWriteTokens: 2_000_000, cacheWrite1hTokens: 1_000_000);

        Assert.AreEqual(2.00 + 0.20 + 2.50 + 4.00, cost, 0.0001);
    }

    [TestMethod]
    public void EstimateCost_WithoutCachedRate_PricesReadsAtFullInput()
    {
        // gpt-5.5-pro has no cachedInputPerM: no discount is assumed.
        using var estimator = BuiltIn();

        Assert.AreEqual(
            estimator.EstimateCost("gpt-5.5-pro", 1_000_000, 0),
            estimator.EstimateCost("gpt-5.5-pro", 1_000_000, 0, 800_000, 0, 0), 0.0001);
    }

    [TestMethod]
    public void EstimateCost_GptCacheReads_AreDiscounted()
    {
        // #845: a scheduled subagent run on gpt-5.6-sol — 2,503,614 input of which 2,312,031
        // cached, 7,073 output. Before CachedInputPerM it was ledgered at $12.73.
        using var estimator = BuiltIn();

        var cost = estimator.EstimateCost("gpt-5.6-sol-2026-07-09",
            inputTokens: 2_503_614, outputTokens: 7_073,
            cachedInputTokens: 2_312_031, cacheWriteTokens: 0, cacheWrite1hTokens: 0);

        // 191,583 uncached × $5 + 2,312,031 cached × $0.50 + 7,073 out × $30, per million.
        Assert.AreEqual((191_583 * 5.00 + 2_312_031 * 0.50 + 7_073 * 30.00) / 1_000_000.0, cost, 0.0001);
        Assert.IsTrue(cost < 2.50, $"expected ~$2.33, got {cost:F2}");
    }

    [TestMethod]
    [DataRow("gpt-5.6-sol")]
    [DataRow("gpt-5.6-terra")]
    [DataRow("gpt-5.6-luna")]
    [DataRow("gpt-5.5")]
    [DataRow("gpt-5.4")]
    [DataRow("gpt-5.4-mini")]
    public void BuiltInDefaults_GptModels_PriceCacheReadsAtTenPercent(string modelId)
    {
        using var estimator = BuiltIn();
        var entry = estimator.FindEntry(modelId)!;

        Assert.IsNotNull(entry.CachedInputPerM, $"{modelId} has no cachedInputPerM");
        Assert.AreEqual(entry.InputPerM * 0.10, entry.CachedInputPerM.Value, 0.0001);
    }

    [TestMethod]
    public void ShippedPricingFile_MatchesBuiltInDefaults()
    {
        // The image seeds the PVC with llm-pricing.json; the built-in table is the fallback when
        // that file is missing. They must price every model the same, or estimates change with
        // which one happens to be loaded (#845 had to be fixed in both).
        var shippedPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "llm-pricing.json");
        using var shipped = NewEstimator(shippedPath);
        using var builtIn = BuiltIn();

        Assert.AreEqual(shippedPath, shipped.LoadedFrom, "shipped pricing file did not load");
        CollectionAssert.AreEquivalent(
            builtIn.Entries.ToList(), shipped.Entries.ToList(),
            "llm-pricing.json and LlmCostEstimator.BuiltInDefaults disagree");
    }

    [TestMethod]
    public void EstimateCost_FromUsageDetails_ReadsAnthropicCounts()
    {
        using var estimator = BuiltIn();
        var usage = new Microsoft.Extensions.AI.UsageDetails
        {
            InputTokenCount = 3_000_000,
            CachedInputTokenCount = 1_000_000,
            OutputTokenCount = 0,
            AdditionalCounts = new()
            {
                ["CacheCreationInputTokens"] = 1_000_000,
                ["CacheCreation1hInputTokens"] = 1_000_000,
            },
        };

        // 1M uncached ($2) + 1M read ($0.20) + 1M 1h-write ($4).
        Assert.AreEqual(6.20, estimator.EstimateCost("claude-sonnet-5-5", usage), 0.0001);
    }

    [TestMethod]
    public void EstimateCost_FileEntry_HonoursExplicitCacheRates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pricing-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            [
              { "prefix": "m", "inputPerM": 10.0, "outputPerM": 0.0,
                "cachedInputPerM": 1.0, "cacheWrite5mPerM": 11.0, "cacheWrite1hPerM": 15.0 }
            ]
            """);
        try
        {
            using var estimator = NewEstimator(path);

            Assert.AreEqual(1.0 + 11.0 + 15.0,
                estimator.EstimateCost("m", 3_000_000, 0, 1_000_000, 2_000_000, 1_000_000), 0.0001);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void FindEntry_IsTheRowEstimateCostUses()
    {
        using var estimator = NewEstimator(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.AreEqual("gpt-5.4-pro", estimator.FindEntry("azure/GPT-5.4-PRO-2026")!.Prefix, "longest prefix, any case");
        Assert.AreEqual("gpt-5.4", estimator.FindEntry("gpt-5.4")!.Prefix);
        Assert.IsNull(estimator.FindEntry("totally-unknown-model"));
    }

    [TestMethod]
    public void EntriesAndLoadedFrom_DescribeTheTableInUse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pricing-{Guid.NewGuid():N}.json");
        using (var builtIn = NewEstimator(path))
        {
            Assert.IsNull(builtIn.LoadedFrom, "no file: the built-in table");
            Assert.IsTrue(builtIn.Entries.Count > 10);
        }

        File.WriteAllText(path, """[ { "prefix": "a", "inputPerM": 1, "outputPerM": 2 }, { "prefix": "abc", "inputPerM": 3, "outputPerM": 4 } ]""");
        try
        {
            using var fromFile = NewEstimator(path);

            Assert.AreEqual(path, fromFile.LoadedFrom);
            CollectionAssert.AreEqual(new[] { "abc", "a" }, fromFile.Entries.Select(e => e.Prefix).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
