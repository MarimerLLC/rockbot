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
