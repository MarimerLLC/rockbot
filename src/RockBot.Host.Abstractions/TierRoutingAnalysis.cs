namespace RockBot.Host;

/// <summary>
/// Pre-aggregated analysis of a tier-routing log window. Produced by
/// <see cref="TierRoutingAnalyzer.Analyze"/> and consumed by the dream
/// routing-review pass and the introspection MCP server's summary tool.
/// <para>
/// The whole point of this record is to do the statistical heavy lifting
/// in code so the LLM downstream can spend its tokens on judgment
/// (validating flagged clusters, filtering keyword candidates) instead
/// of recomputing aggregates from raw entries.
/// </para>
/// </summary>
public sealed record TierRoutingAnalysis(
    int SchemaVersion,
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd,
    int TotalEntries,
    int FallbackExcludedCount,
    TierRoutingGlobalStats GlobalStats,
    IReadOnlyList<TierRoutingCluster> Clusters,
    IReadOnlyList<TierRoutingFlaggedCluster> FlaggedClusters,
    TierRoutingKeywordCandidates KeywordCandidates,
    IReadOnlyList<TierRoutingThresholdScan> ThresholdScans,
    TierRoutingProjectedCost ProjectedCost);

public sealed record TierRoutingGlobalStats(
    IReadOnlyList<TierRoutingTierStats> ByTier,
    int UserMessageCount,
    int SubagentCount,
    int FallbackCount,
    double FallbackPct);

public sealed record TierRoutingTierStats(
    ModelTier Tier,
    int Count,
    double Pct,
    double AvgComplexityScore,
    long? AvgLatencyMs,
    long? AvgInputTokens,
    long? AvgOutputTokens,
    double AvgToolCalls);

/// <summary>
/// A group of entries with the same routing-shape signature (keyword set + tier + tool-call bucket).
/// Used as the unit of LLM-visible analysis — N similar entries collapse into 1 line in the prompt.
/// </summary>
public sealed record TierRoutingCluster(
    string Signature,
    ModelTier Tier,
    int Count,
    string SamplePrompt,
    double AvgComplexityScore,
    double AvgToolCalls,
    long? AvgInputTokens,
    long? AvgOutputTokens,
    int? AvgPostInjectionTokens,
    int FallbackCount,
    IReadOnlyList<string> MatchedHighKeywords,
    IReadOnlyList<string> MatchedLowKeywords);

/// <summary>
/// A cluster that tripped one of the deterministic detection rules. The LLM's job is to
/// validate whether this is a true misroute (vs. noise) and decide what to do about it.
/// </summary>
public sealed record TierRoutingFlaggedCluster(
    TierRoutingCluster Cluster,
    string Flag,
    string Rationale,
    decimal? ProjectedCostCurrentTier,
    decimal? ProjectedCostAlternateTier,
    ModelTier? AlternateTier);

public sealed record TierRoutingKeywordCandidates(
    IReadOnlyList<TierRoutingKeywordCandidate> HighSignalCandidates,
    IReadOnlyList<TierRoutingKeywordCandidate> LowSignalCandidates);

public sealed record TierRoutingKeywordCandidate(
    string Keyword,
    int HighTierCount,
    int BalancedTierCount,
    int LowTierCount,
    double FrequencyRatio);

/// <summary>
/// A "what if" projection: counts how many entries in the window would flip tier
/// if the threshold moved by the given delta. Lets the LLM pick a shift with
/// deterministic impact data instead of guessing.
/// </summary>
public sealed record TierRoutingThresholdScan(
    string Threshold,
    double Delta,
    int EntriesFlipped,
    string DirectionDescription,
    IReadOnlyList<string> SamplePrompts,
    decimal? ProjectedCostDelta);

public sealed record TierRoutingProjectedCost(
    decimal TotalUsd,
    IReadOnlyList<TierRoutingTierCost> ByTier,
    int EntriesPricedCount,
    int EntriesUnpricedCount);

public sealed record TierRoutingTierCost(
    ModelTier Tier,
    decimal Usd);
