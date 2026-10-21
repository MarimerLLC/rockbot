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
    IReadOnlyList<string> MatchedLowKeywords)
{
    /// <summary>
    /// The rule that decided <see cref="Tier"/> — one of the <see cref="TierRoutingRules"/>
    /// constants. Logged with every routing decision so the routing dream, and a human
    /// reading the log, can tell a score-band decision from a thread-state floor. See #663.
    /// </summary>
    public string Rule { get; init; } = TierRoutingRules.ScoreBand;

    /// <summary>
    /// The tier the message earns on its own — score band, gates, and the content floors
    /// (research question, balanced-floor keyword) — before thread-state rules (the
    /// active-subagent floor, the inherited tier) raise it (#729). Callers
    /// that keep a per-session tier history record this value, not <see cref="Tier"/>, so an
    /// inherited tier decays instead of perpetuating itself. Null when the selector does not
    /// distinguish the two (treat as <see cref="Tier"/>).
    /// </summary>
    public ModelTier? IntrinsicTier { get; init; }

    /// <summary>
    /// How far <see cref="ComplexityScore"/> is from the nearest tier ceiling. Small means the
    /// keyword score could have gone either way; the decision-model refiner asks about those.
    /// Null when the selector has no score bands.
    /// </summary>
    public double? BoundaryDistance { get; init; }
}

/// <summary>
/// Names of the rules that can decide a tier-routing outcome. Values are stable strings
/// written to logs and to <see cref="TierRoutingEntry.RoutingRule"/>. See #663.
/// </summary>
public static class TierRoutingRules
{
    /// <summary>The complexity score's band (Low / Balanced / High ceilings) decided the tier.</summary>
    public const string ScoreBand = "score-band";

    /// <summary>The score reached High but no complexity signal was present, so the tier was capped at Balanced.</summary>
    public const string HighGate = "high-gate";

    /// <summary>The trivial guard forced an objectively simple prompt down to Low.</summary>
    public const string TrivialGuard = "trivial-guard";

    /// <summary>
    /// A follow-up on an established thread was floored at Balanced. No longer emitted since
    /// #729 — the inherited tier covers threads that earned Balanced — but kept so older
    /// routing-log entries stay readable.
    /// </summary>
    public const string ActiveThreadFloor = "active-thread-floor";

    /// <summary>The session had a subagent running, so the turn was floored at Balanced.</summary>
    public const string ActiveSubagentFloor = "active-subagent-floor";

    /// <summary>The turn inherited the highest tier routed for the session in its recent turns.</summary>
    public const string InheritedTier = "inherited-tier";

    /// <summary>The keyword score sat near a tier boundary and the decision model picked the tier.</summary>
    public const string DecisionModel = "decision-model";

    /// <summary>A dream-learned balanced-floor keyword escalated Low to Balanced (#486).</summary>
    public const string BalancedFloorKeyword = "balanced-floor-keyword";

    /// <summary>A research-shaped question naming a technical subject was floored at Balanced.</summary>
    public const string ResearchQuestionFloor = "research-question-floor";

    /// <summary>A pure acknowledgement/greeting was allowed to stay Low despite thread-state floors.</summary>
    public const string TrivialAck = "trivial-ack";

    /// <summary>The selector is pinned to one tier (<c>LLM:FixedTier</c>).</summary>
    public const string Fixed = "fixed";
}

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
/// See issue #383. Defaults to <c>false</c>. From #663 until #729 every non-ack user message on
/// an established thread was floored at Balanced; <c>KeywordTierSelector</c> no longer
/// routes on it — a thread stays Balanced through <paramref name="RecentMaxTier"/> only while
/// a recent turn earned it. Still logged with each routing decision.
/// </param>
/// <param name="RecentMaxTier">
/// The highest tier this session's recent turns earned (bounded by count and by
/// <see cref="ShortMessageHeuristics.ThreadEstablishedRecency"/>), or null when there is no
/// recent history. A turn earns its intrinsic tier, raised when it escalated mid-turn or did
/// work on the user's behalf. A user message inherits it unless it is a pure acknowledgement.
/// See #663, #729.
/// </param>
/// <param name="ActiveSubagent">
/// True when the session has at least one subagent still running. A user message is then
/// floored at Balanced unless it is a pure acknowledgement. See #663.
/// </param>
public sealed record TierRoutingContext(
    string? Origin = null,
    bool ThreadEstablished = false,
    ModelTier? RecentMaxTier = null,
    bool ActiveSubagent = false);

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
