namespace RockBot.Host;

/// <summary>
/// A single tier-routing decision record written to <c>tier-routing-log.jsonl</c>.
/// </summary>
public sealed record TierRoutingEntry(
    DateTimeOffset Timestamp,
    string PromptPreview,
    ModelTier Tier,
    string Context);
