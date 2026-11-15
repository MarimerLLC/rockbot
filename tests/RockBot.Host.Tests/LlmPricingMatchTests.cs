using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// A pricing row matches a model id whichever way the provider spells the version: the Anthropic
/// API's <c>claude-haiku-5-5</c> and OpenRouter's <c>anthropic/claude-haiku-5.5</c> are one model.
/// </summary>
[TestClass]
public sealed class LlmPricingMatchTests
{
    [TestMethod]
    [DataRow("claude-haiku-5-5", "claude-haiku-5-5")]
    [DataRow("anthropic/claude-haiku-5.5", "claude-haiku-5-5")]
    [DataRow("anthropic/claude-opus-5.5", "claude-opus-5-5")]
    [DataRow("gpt-5.6-terra", "gpt-5.6-terra")]
    [DataRow("gpt-5-6-terra", "gpt-5.6-terra")]
    [DataRow("Azure/GPT-5.4-PRO-2026", "gpt-5.4-pro")]
    [DataRow("anything", "")]
    public void Matches(string modelId, string prefix) =>
        Assert.IsTrue(LlmPricingMatch.Matches(modelId, prefix), $"{prefix} should price {modelId}");

    [TestMethod]
    [DataRow("claude-haiku-5", "claude-haiku-5-5")]
    [DataRow("claude-haiku-4.5", "claude-haiku-5-5")]
    [DataRow("gpt-5.6", "gpt-5.6-terra")]
    [DataRow("claude-haiku_5_5", "claude-haiku-5-5")]
    [DataRow("", "gpt-5.4")]
    public void DoesNotMatch(string modelId, string prefix) =>
        Assert.IsFalse(LlmPricingMatch.Matches(modelId, prefix), $"{prefix} should not price {modelId}");

    [TestMethod]
    public void TheEstimator_CostsAnOpenRouterFallbackLikeTheDirectModel()
    {
        using var estimator = new LlmCostEstimator(
            Options.Create(new LlmPricingOptions { ConfigPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json") }),
            NullLogger<LlmCostEstimator>.Instance);

        // The live Balanced chain: claude-haiku-5-5 on the Anthropic API, then the same model on OpenRouter.
        var direct = estimator.EstimateCost("claude-haiku-5-5", 1_000_000, 1_000_000);
        var viaOpenRouter = estimator.EstimateCost("anthropic/claude-haiku-5.5", 1_000_000, 1_000_000);

        Assert.AreEqual(0.60, direct, 1e-9);
        Assert.AreEqual(direct, viaOpenRouter, 1e-9, "the OpenRouter fallback used to cost $0");
    }

    [TestMethod]
    public void LongestPrefixStillWins_AcrossSpellings()
    {
        using var estimator = new LlmCostEstimator(
            Options.Create(new LlmPricingOptions { ConfigPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json") }),
            NullLogger<LlmCostEstimator>.Instance);

        // claude-opus-5-5 ($4) must win over claude-opus-5 ($5) for OpenRouter's dotted id too.
        Assert.AreEqual(4.00, estimator.EstimateCost("anthropic/claude-opus-5.5", 1_000_000, 0), 1e-9);
    }
}
