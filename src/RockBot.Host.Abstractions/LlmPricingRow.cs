namespace RockBot.Host;

/// <summary>
/// Pricing for a single model prefix, matching the schema of <c>llm-pricing.json</c>.
/// Cost = (inputTokens * <see cref="InputPerM"/> + outputTokens * <see cref="OutputPerM"/>) / 1_000_000.
/// The first row whose <see cref="Prefix"/> is contained in the model ID wins
/// (longest-prefix-first ordering, enforced by the on-disk file).
/// </summary>
public sealed record LlmPricingRow(
    string Prefix,
    decimal InputPerM,
    decimal OutputPerM);
