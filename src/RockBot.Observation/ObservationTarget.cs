using RockBot.Host;

namespace RockBot.Observation;

/// <summary>
/// Per-target configuration for the observation framework. One instance is
/// registered for each target the agent maintains (theory-of-self, theory-of-user,
/// future targets). The framework's pipeline service iterates registered targets
/// during a dream cycle and runs the same pipeline for each.
/// </summary>
public sealed class ObservationTarget
{
    /// <summary>
    /// Stable target identifier used in metric tags, log lines, and JSON state
    /// filenames. Should be lowercase-kebab, e.g. "theory-of-self".
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Filter that scopes what conversation turns this target observes.
    /// </summary>
    public required ITranscriptFilter Filter { get; init; }

    /// <summary>
    /// Whether the per-conversation behavior summary (tool-call counts,
    /// iteration counts, retried-after-error, etc.) is included as
    /// augmentation alongside the filtered turns. Useful for theory-of-self
    /// (where behavior is the subject); not useful for theory-of-user (where
    /// the user never sees the agent's tool calls).
    /// </summary>
    public bool IncludeBehaviorSummary { get; init; }

    /// <summary>
    /// Prompt fed to the extraction LLM call. The framework will append the
    /// formatted transcript and behavior summary; this prompt sets the
    /// extraction stance and grounding rules.
    /// </summary>
    public required string ExtractionPrompt { get; init; }

    /// <summary>
    /// Prompt fed to the evaluation LLM call. Differential framing: each
    /// candidate is verified against existing theories rather than generated
    /// freely.
    /// </summary>
    public required string EvaluationPrompt { get; init; }

    /// <summary>
    /// Absolute or PVC-relative path to the JSON state file. Source of truth
    /// for the target.
    /// </summary>
    public required string StateFilePath { get; init; }

    /// <summary>
    /// Absolute or PVC-relative path to the regenerated markdown output.
    /// Overwritten each dream by the phase-2 template render.
    /// </summary>
    public required string OutputMarkdownPath { get; init; }

    /// <summary>
    /// LLM tier used for per-conversation observation extraction. Cheap tier
    /// is appropriate for high-recall mechanical extraction.
    /// </summary>
    public ModelTier ExtractionTier { get; init; } = ModelTier.Low;

    /// <summary>
    /// LLM tier used for evaluation/promotion. Higher tier earns its cost
    /// here: the evaluation pass is judgment work over a small candidate set.
    /// </summary>
    public ModelTier EvaluationTier { get; init; } = ModelTier.Balanced;

    /// <summary>
    /// Number of distinct conversations a candidate must be reinforced from
    /// before it is eligible for promotion. Default 3; higher values for
    /// noisier targets (theory-of-self) and lower values for denser-signal
    /// targets (theory-of-user).
    /// </summary>
    public int PromotionThreshold { get; init; } = 3;

    /// <summary>
    /// Number of days a candidate may go without new references before it is
    /// dropped. Calendar-time aging is cadence-independent: changing the dream
    /// cron schedule does not silently change how aggressively candidates fade.
    /// </summary>
    public int CandidateAgingWindowDays { get; init; } = 7;

    /// <summary>
    /// Number of days a theory may go without new supporting references
    /// before it is dropped. Should be substantially longer than
    /// <see cref="CandidateAgingWindowDays"/> — promoted theories represent
    /// durable observations and should not fade quickly just because the
    /// agent hasn't had relevant conversations recently.
    /// </summary>
    public int TheoryAgingWindowDays { get; init; } = 30;

    /// <summary>
    /// Vector-similarity cosine threshold for matching new observations to
    /// existing candidate clusters. Higher = stricter (fewer false merges,
    /// more singleton candidates). Default 0.85; calibration is per-target
    /// and per-embedding-model.
    /// </summary>
    public float ClusteringSimilarityThreshold { get; init; } = 0.85f;

    /// <summary>
    /// Maximum snapshots retained in <see cref="ObservationState.Snapshots"/>.
    /// At twice-daily dreaming, 12 snapshots cover roughly the last week.
    /// </summary>
    public int SnapshotRetentionCount { get; init; } = 12;
}
