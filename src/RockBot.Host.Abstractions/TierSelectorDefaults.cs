namespace RockBot.Host;

/// <summary>
/// The tier selector's compiled thresholds and guardrail ranges: the one source of truth for
/// <c>KeywordTierSelector</c> (which routes) and <see cref="TierRoutingAnalyzer"/> plus the
/// tier-routing dream pass (which tune it). A null field in <c>tier-selector.json</c> means
/// "use the compiled default", so anything that reasons about the config has to resolve nulls
/// the way the selector does. When the analyzer kept its own copy, the copies drifted (#844):
/// the dream read a null <c>balancedCeiling</c> as 0.46 while the selector routed on 0.55,
/// and a "raise" it proposed was really a decrease.
/// </summary>
public static class TierSelectorDefaults
{
    public const double LowCeiling = 0.25;
    public const double BalancedCeiling = 0.55;
    public const double TrivialGuardCeiling = 0.15;
    public const double UserOriginBias = 0.10;

    // Guardrails: dream-tuned values are clamped to these ranges.
    public const double MinLowCeiling = 0.15;
    public const double MaxLowCeiling = 0.40;
    public const double MinBalancedCeiling = 0.40;
    public const double MaxBalancedCeiling = 0.80;
    public const double MinTrivialGuardCeiling = 0.10;
    public const double MaxTrivialGuardCeiling = 0.25;
    public const double MinUserOriginBias = 0.0;
    public const double MaxUserOriginBias = 0.20;

    /// <summary>The <c>lowCeiling</c> the selector routes on for <paramref name="config"/>.</summary>
    public static double EffectiveLowCeiling(TierSelectorConfig? config) =>
        Resolve(config?.LowCeiling, LowCeiling, MinLowCeiling, MaxLowCeiling);

    /// <summary>The <c>balancedCeiling</c> the selector routes on for <paramref name="config"/>.</summary>
    public static double EffectiveBalancedCeiling(TierSelectorConfig? config) =>
        Resolve(config?.BalancedCeiling, BalancedCeiling, MinBalancedCeiling, MaxBalancedCeiling);

    /// <summary>The <c>trivialGuardCeiling</c> the selector routes on for <paramref name="config"/>.</summary>
    public static double EffectiveTrivialGuardCeiling(TierSelectorConfig? config) =>
        Resolve(config?.TrivialGuardCeiling, TrivialGuardCeiling, MinTrivialGuardCeiling, MaxTrivialGuardCeiling);

    /// <summary>The <c>userOriginBias</c> the selector routes on for <paramref name="config"/>.</summary>
    public static double EffectiveUserOriginBias(TierSelectorConfig? config) =>
        Resolve(config?.UserOriginBias, UserOriginBias, MinUserOriginBias, MaxUserOriginBias);

    /// <summary>
    /// <paramref name="value"/> clamped to [<paramref name="min"/>, <paramref name="max"/>], or
    /// <paramref name="compiledDefault"/> when it is null — how the selector resolves a field.
    /// </summary>
    public static double Resolve(double? value, double compiledDefault, double min, double max) =>
        value is { } v ? Math.Clamp(v, min, max) : compiledDefault;
}
