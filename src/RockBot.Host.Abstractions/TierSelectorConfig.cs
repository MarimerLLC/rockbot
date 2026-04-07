namespace RockBot.Host;

/// <summary>
/// Hot-reloadable configuration for <c>KeywordTierSelector</c>.
/// Loaded from <c>{AgentBasePath}/tier-selector.json</c> every 60 seconds.
/// All fields are nullable — null means "use the compiled default".
/// </summary>
public sealed class TierSelectorConfig
{
    public int Version { get; set; } = 1;

    /// <summary>Human-readable notes about when/why this config was last changed.</summary>
    public string? Notes { get; set; }

    /// <summary>Score at or below which a prompt is routed to the Low tier.</summary>
    public double? LowCeiling { get; set; }

    /// <summary>Score at or below which a prompt is routed to the Balanced tier.</summary>
    public double? BalancedCeiling { get; set; }

    /// <summary>
    /// Additional keywords that push the score toward the High tier.
    /// Merged with compiled defaults (additions only — compiled defaults cannot be removed via config).
    /// </summary>
    public List<string>? HighSignalKeywords { get; set; }

    /// <summary>
    /// Additional keywords that push the score toward the Low tier.
    /// Merged with compiled defaults (additions only — compiled defaults cannot be removed via config).
    /// </summary>
    public List<string>? LowSignalKeywords { get; set; }

    /// <summary>
    /// Score threshold below which the trivial guard forces Low tier, regardless of
    /// dream-tuned thresholds. Prevents simple prompts from drifting into Balanced
    /// when the dream loop adjusts <see cref="LowCeiling"/>. Default: 0.15.
    /// </summary>
    public double? TrivialGuardCeiling { get; set; }

    /// <summary>
    /// Score reduction applied to user-originated messages (pushes toward lower tiers).
    /// Subagent tasks are not biased. Default: 0.10.
    /// </summary>
    public double? UserOriginBias { get; set; }
}
