namespace RockBot.Host;

/// <summary>
/// The result of classifying a prompt for tier routing.
/// Captures both the routing decision and the signals that drove it so the
/// dream feedback loop can detect mis-routing patterns over time.
/// </summary>
/// <param name="Tier">The selected model tier.</param>
/// <param name="ComplexityScore">Composite score (typically in [-0.15, 1]) that drove the decision. Negative values indicate strong low-signal keyword matches.</param>
/// <param name="MatchedHighKeywords">High-complexity keywords found in the prompt.</param>
/// <param name="MatchedLowKeywords">Simplicity keywords found in the prompt.</param>
public sealed record TierClassification(
    ModelTier Tier,
    double ComplexityScore,
    IReadOnlyList<string> MatchedHighKeywords,
    IReadOnlyList<string> MatchedLowKeywords);

/// <summary>
/// Optional context passed to the tier selector to influence routing beyond prompt text.
/// </summary>
/// <param name="Origin">
/// Origin of the request: <c>"user-message"</c> or <c>"subagent"</c>.
/// User-originated messages receive a bias toward lower tiers since their prompts
/// are semantically simpler even when post-injection context is large.
/// </param>
/// <param name="ThreadEstablished">
/// True when the caller has determined that an active topical thread already exists
/// for this session (typically: prior turns within a recent time window). Short
/// follow-up messages on an established thread benefit from Balanced-tier capacity
/// to weigh recent history against injected memory — without this, smaller Low-tier
/// models tend to summarise injected memory instead of continuing the thread.
/// See issue #383. Defaults to <c>false</c>, preserving prior routing behaviour.
/// </param>
public sealed record TierRoutingContext(string? Origin = null, bool ThreadEstablished = false);

/// <summary>
/// Selects the appropriate <see cref="ModelTier"/> for a given prompt.
/// Implementations may use keyword heuristics, embeddings, or fixed rules.
/// </summary>
public interface ILlmTierSelector
{
    /// <summary>
    /// Returns the tier best suited for <paramref name="promptText"/>.
    /// </summary>
    ModelTier SelectTier(string promptText);

    /// <summary>
    /// Classifies <paramref name="promptText"/> and returns both the routing decision
    /// and the classification signals that drove it. Used by the routing telemetry
    /// pipeline so the dream feedback loop can detect mis-routing patterns.
    /// </summary>
    TierClassification Classify(string promptText);

    /// <summary>
    /// Classifies <paramref name="promptText"/> with additional routing context (origin, etc.)
    /// and returns both the routing decision and the classification signals that drove it.
    /// </summary>
    TierClassification Classify(string promptText, TierRoutingContext context);
}
