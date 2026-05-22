namespace RockBot.Host;

/// <summary>
/// A single tier-routing decision record written to <c>tier-routing-log.jsonl</c>.
/// <para>
/// Classification is performed on the raw user prompt (Option A — pre-injection).
/// <see cref="PostInjectionTokenEstimate"/> captures the estimated token count after
/// memory recall, skill injection, and tool guide expansion, enabling the dream pass
/// to detect "token surprise" misroutes where a prompt that looked simple expanded
/// significantly after context injection.
/// </para>
/// </summary>
public sealed record TierRoutingEntry
{
    // ── Core routing decision ──────────────────────────────────────────────────

    public DateTimeOffset Timestamp { get; init; }
    public string PromptPreview { get; init; } = "";
    public ModelTier Tier { get; init; }

    /// <summary>Origin of the routing request: "user-message" or "subagent".</summary>
    public string Context { get; init; } = "";

    // ── Pre-injection classification (Option A: raw user prompt only) ─────────

    /// <summary>Composite complexity score (typically [-0.15, 1]) that drove the routing decision. Negative values indicate strong low-signal keyword matches.</summary>
    public double ComplexityScore { get; init; }

    /// <summary>High-complexity signal keywords matched in the prompt (push toward High tier).</summary>
    public IReadOnlyList<string> MatchedHighKeywords { get; init; } = [];

    /// <summary>Simplicity signal keywords matched in the prompt (push toward Low tier).</summary>
    public IReadOnlyList<string> MatchedLowKeywords { get; init; } = [];

    // ── Post-injection context size ────────────────────────────────────────────

    /// <summary>
    /// Estimated token count of the full context sent to the LLM, after memory recall,
    /// skill injection, and tool guide expansion. Computed as total character count / 4.
    /// The dream pass uses the delta between this and the pre-injection classification to
    /// detect systematic token-surprise misroutes. Null when not captured.
    /// </summary>
    public int? PostInjectionTokenEstimate { get; init; }

    // ── LLM response telemetry ─────────────────────────────────────────────────

    /// <summary>
    /// The model that handled this routing decision. Populated from the chat response's
    /// <c>ModelId</c> when available, falling back to the tier's configured default model.
    /// Used by the routing analyzer to compute per-entry USD cost by joining with the
    /// pricing table. Null on entries written before this field existed.
    /// </summary>
    public string? ModelId { get; init; }

    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? LatencyMs { get; init; }

    // ── Tool call telemetry ────────────────────────────────────────────────────

    public int? ToolCallCount { get; init; }
    public IReadOnlyList<string>? ToolsUsed { get; init; }

    // ── Fallback indicator ─────────────────────────────────────────────────────

    /// <summary>
    /// True when a model fallback was triggered due to quota exhaustion or a hard API
    /// error rather than a genuine quality-based misroute. The dream pass excludes
    /// fallback sessions from the quality-signal training set to avoid polluting
    /// routing heuristics with infrastructure noise.
    /// </summary>
    public bool IsFallbackTriggered { get; init; }
}
