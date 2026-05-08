using RockBot.Host;

namespace RockBot.Observation;

/// <summary>
/// Factory helpers for the framework's standard targets. Hosts that want to
/// run the default theory-of-self / theory-of-user observation pair register
/// the targets produced here; hosts wanting different defaults construct
/// their own <see cref="ObservationTarget"/> instances directly.
/// </summary>
public static class ObservationDefaults
{
    /// <summary>
    /// Default target name for the theory-of-self observation pair.
    /// Used as the JSON state filename stem and as the markdown filename stem.
    /// </summary>
    public const string TheoryOfSelfName = "theory-of-self";

    /// <summary>Default target name for theory-of-user.</summary>
    public const string TheoryOfUserName = "theory-of-user";

    /// <summary>
    /// Constructs the default theory-of-self target rooted at the given
    /// agent-profile directory. Both the JSON state and the regenerated
    /// markdown live under <c>{agentProfileBasePath}/observation/</c> —
    /// these are inspection artifacts, not agent-profile files. The host's
    /// profile loader does not auto-inject this markdown into context;
    /// research-mode v1 is "collect, don't inject."
    /// </summary>
    public static ObservationTarget CreateTheoryOfSelf(string agentProfileBasePath) =>
        new()
        {
            Name = TheoryOfSelfName,
            Filter = TranscriptFilters.Everything,
            ExtractionPrompt = DefaultPrompts.TheoryOfSelfExtraction,
            EvaluationPrompt = DefaultPrompts.DifferentialEvaluation,
            StateFilePath = Path.Combine(agentProfileBasePath, "observation", $"{TheoryOfSelfName}.json"),
            OutputMarkdownPath = Path.Combine(agentProfileBasePath, "observation", $"{TheoryOfSelfName}.md"),
            ExtractionTier = ModelTier.Low,
            EvaluationTier = ModelTier.Balanced,
            // The design opted into behaviour-summary input for theory-of-self;
            // the actual summary computation is a future enhancement (see
            // design open questions). The flag is set true now so the orchestrator
            // wiring is in place when the summary is added.
            IncludeBehaviorSummary = true,
        };

    /// <summary>
    /// Constructs the default theory-of-user target rooted at the given
    /// agent-profile directory. Uses the user-authored transcript filter:
    /// observes user-authored turns and the agent's user-facing replies,
    /// excluding scheduled-task and heartbeat activity. Output lives under
    /// <c>{agentProfileBasePath}/observation/</c>; not auto-injected into
    /// context (research-mode v1 is collect-only).
    /// </summary>
    /// <remarks>
    /// Theory-of-user uses a denser-signal threshold (default unchanged
    /// from the type's default of 3 reinforcements) — every user turn is
    /// a deliberate human choice, so signal per turn is high.
    /// </remarks>
    public static ObservationTarget CreateTheoryOfUser(string agentProfileBasePath) =>
        new()
        {
            Name = TheoryOfUserName,
            Filter = TranscriptFilters.UserAuthored,
            ExtractionPrompt = DefaultPrompts.TheoryOfUserExtraction,
            EvaluationPrompt = DefaultPrompts.DifferentialEvaluation,
            StateFilePath = Path.Combine(agentProfileBasePath, "observation", $"{TheoryOfUserName}.json"),
            OutputMarkdownPath = Path.Combine(agentProfileBasePath, "observation", $"{TheoryOfUserName}.md"),
            ExtractionTier = ModelTier.Low,
            EvaluationTier = ModelTier.Balanced,
            IncludeBehaviorSummary = false,
        };
}
