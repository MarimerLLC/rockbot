namespace RockBot.Host;

/// <summary>
/// Options for the feedback capture and session evaluation system.
/// </summary>
public sealed class FeedbackOptions
{
    /// <summary>
    /// Whether the session-summary evaluator runs. Defaults to <c>true</c>.
    /// <para>
    /// Set to <c>false</c> for agents where per-session self-evaluation is not wanted. It is
    /// a background LLM consumer that fires on every idle session independently of user
    /// activity, so on a rate-limited or per-token-billed endpoint it can quietly dominate
    /// spend — and on a single-provider model it can exhaust the quota the user needs to
    /// hold a conversation.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; } = true;

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
