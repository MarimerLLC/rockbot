namespace RockBot.Host;

/// <summary>
/// Options for <see cref="LlmCostEstimator"/>. Pricing is loaded from a JSON file
/// on the agent PVC so prices can be refreshed without rebuilding the image.
/// </summary>
public sealed class LlmPricingOptions
{
    /// <summary>
    /// Absolute path to the pricing JSON file. When the file is missing or fails to
    /// parse, the estimator falls back to a small built-in table.
    /// </summary>
    public string ConfigPath { get; set; } = "/data/agent/llm-pricing.json";
}
