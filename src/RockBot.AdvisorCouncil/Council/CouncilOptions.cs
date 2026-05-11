namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// Configuration for the council pipeline, bound from the "Council" config section.
/// </summary>
internal sealed class CouncilOptions
{
    /// <summary>Absolute path to the personas directory. Empty falls back to agent/personas next to the binary.</summary>
    public string PersonasPath { get; set; } = string.Empty;

    /// <summary>Hard wall-clock cap on a council run before cancellation.</summary>
    public int OverallTimeoutSeconds { get; set; } = 180;

    /// <summary>Per-persona soft timeout. On expiry the persona view becomes "(timed out)" and synthesis proceeds without it.</summary>
    public int PerPersonaTimeoutSeconds { get; set; } = 60;

    /// <summary>Pre-research stage timeout.</summary>
    public int PreResearchTimeoutSeconds { get; set; } = 90;

    /// <summary>ResearchAgent invocation timeout (used in Phase 3 by ResearchAgentInvoker).</summary>
    public int ResearchAgentTimeoutSeconds { get; set; } = 90;
}
