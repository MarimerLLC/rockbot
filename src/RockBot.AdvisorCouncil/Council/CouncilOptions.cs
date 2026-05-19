namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// Configuration for the council pipeline, bound from the "Council" config section.
/// </summary>
internal sealed class CouncilOptions
{
    /// <summary>Absolute path to the personas directory. Empty falls back to agent/personas next to the binary.</summary>
    public string PersonasPath { get; set; } = string.Empty;

    /// <summary>Hard wall-clock cap on a council run before cancellation.</summary>
    public int OverallTimeoutSeconds { get; set; } = 300;

    /// <summary>Per-persona soft timeout. On expiry the persona view becomes "(timed out)" and synthesis proceeds without it.</summary>
    public int PerPersonaTimeoutSeconds { get; set; } = 120;

    /// <summary>Pre-research stage timeout.</summary>
    public int PreResearchTimeoutSeconds { get; set; } = 90;

    /// <summary>ResearchAgent invocation timeout (used in Phase 3 by ResearchAgentInvoker).</summary>
    public int ResearchAgentTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Maximum number of research tool calls a single persona may make during its view step.
    /// Past this cap, the scoped research tool short-circuits with a budget-exhausted sentinel
    /// so the persona answers from existing context.
    /// </summary>
    public int MaxResearchCallsPerPersona { get; set; } = 3;
}
