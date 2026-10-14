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
        // gpt-5.4 has no cachedInputPerM: no discount is assumed, same as before cache pricing.
        using var estimator = BuiltIn();

        Assert.AreEqual(
            estimator.EstimateCost("gpt-5.4", 1_000_000, 0),
            estimator.EstimateCost("gpt-5.4", 1_000_000, 0, 800_000, 0, 0), 0.0001);
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
}
