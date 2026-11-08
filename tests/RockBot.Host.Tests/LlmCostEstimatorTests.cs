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
    [DataRow("anthropic/claude-sonnet-5.5", 12.00)]
    [DataRow("anthropic/claude-haiku-5.5", 0.60)]
    [DataRow("anthropic/claude-opus-5.5", 24.00)]
    public void EstimateCost_BuiltInDefaults_CoverDeployedClaude5Models(string modelId, double expected)
    {
        // Regression: the previous built-in table had no entry for any 5.x Claude model,
        // so every live call silently priced at $0 — see the daily-briefing-1600-delta
        // incident (2.9M real tokens, $0 reported).
        using var estimator = NewEstimator(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.AreEqual(expected, estimator.EstimateCost(modelId, 1_000_000, 1_000_000), 0.0001);
    }

    [TestMethod]
    public void EstimateCost_LongestPrefixWins_ForClaudePointReleases()
    {
        // claude-sonnet-5.5 must match before claude-sonnet-5, even though the shorter
        // prefix is a literal substring of the longer one.
        using var estimator = NewEstimator(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        var sonnet55 = estimator.EstimateCost("anthropic/claude-sonnet-5.5", 1_000_000, 0);
        var sonnet5 = estimator.EstimateCost("anthropic/claude-sonnet-5", 1_000_000, 0);

        Assert.AreEqual(2.00, sonnet55, 0.0001);
        Assert.AreEqual(2.00, sonnet5, 0.0001);
    }

    [TestMethod]
    public void EstimateCost_AppliesCacheReadDiscount_ToCachedPortionOfInput()
    {
        // anthropic/claude-sonnet-5.5 = $2/M input, $10/M output. 400k of the 1M input
        // tokens were served from cache and bill at 0.1x: (600k*2 + 400k*2*0.1)/1M = 1.28.
        using var estimator = NewEstimator(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        var cost = estimator.EstimateCost("anthropic/claude-sonnet-5.5", 1_000_000, 0, cachedInputTokens: 400_000);

        Assert.AreEqual(1.28, cost, 0.0001);
    }

    [TestMethod]
    public void EstimateCost_WithZeroCachedTokens_MatchesUndiscountedCost()
    {
        using var estimator = NewEstimator(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        var discounted = estimator.EstimateCost("anthropic/claude-sonnet-5.5", 1_000_000, 500_000, cachedInputTokens: 0);
        var undiscounted = estimator.EstimateCost("anthropic/claude-sonnet-5.5", 1_000_000, 500_000);

        Assert.AreEqual(undiscounted, discounted, 0.0001);
    }

    [TestMethod]
    public void EstimateCost_ClampsCachedTokens_WhenReportedLargerThanInput()
    {
        // Defensive: a provider usage quirk reporting cached > input must not go negative
        // or overshoot — clamp to the full input at the discounted rate.
        using var estimator = NewEstimator(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        var cost = estimator.EstimateCost("anthropic/claude-sonnet-5.5", 1_000_000, 0, cachedInputTokens: 5_000_000);

        Assert.AreEqual(0.20, cost, 0.0001);
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
}
