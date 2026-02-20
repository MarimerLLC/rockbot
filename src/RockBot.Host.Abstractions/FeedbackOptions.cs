namespace RockBot.Host;

/// <summary>
/// Options for the feedback capture and session evaluation system.
/// </summary>
public sealed class FeedbackOptions
{
    /// <summary>
    /// Base directory for per-session feedback JSONL files.
    /// Relative paths are resolved under <see cref="AgentProfileOptions.BasePath"/>.
    /// </summary>
    public string BasePath { get; set; } = "feedback";

    /// <summary>
    /// How long a session must be idle (no new turns) before it is considered ended
    /// and eligible for session-summary evaluation.
    /// </summary>
    public TimeSpan SessionIdleThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Path to the session evaluator LLM directive file.
    /// Relative paths are resolved under <see cref="AgentProfileOptions.BasePath"/>.
    /// </summary>
    public string EvaluatorDirectivePath { get; set; } = "session-evaluator.md";

    /// <summary>How often the session summary service polls for sessions to evaluate.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);
}
