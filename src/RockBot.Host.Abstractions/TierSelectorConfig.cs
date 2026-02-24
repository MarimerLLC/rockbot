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

    /// <summary>Complete list of keywords that push the score toward the High tier.</summary>
    public List<string>? HighSignalKeywords { get; set; }

    /// <summary>Complete list of keywords that push the score toward the Low tier.</summary>
    public List<string>? LowSignalKeywords { get; set; }
}
