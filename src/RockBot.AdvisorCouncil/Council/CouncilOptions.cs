using RockBot.Host;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// Configuration for the council pipeline, bound from the "Council" config section.
/// </summary>
internal sealed class CouncilOptions
{
    /// <summary>Absolute path to the personas directory. Empty falls back to agent/personas next to the binary.</summary>
    public string PersonasPath { get; set; } = string.Empty;

    /// <summary>
    /// The model tier of the Select step: choosing personas and toggles from a short brief.
    /// Balanced is plenty, and it runs before anything else, so it sets the floor on latency.
    /// </summary>
    public ModelTier SelectTier { get; set; } = ModelTier.Balanced;

    /// <summary>
    /// The model tier each persona thinks on, for its view and its critique. High: the personas'
    /// reasoning is the council's product.
    /// </summary>
    public ModelTier PersonaTier { get; set; } = ModelTier.High;

    /// <summary>The model tier of the synthesis, which integrates the views. High.</summary>
    public ModelTier SynthesisTier { get; set; } = ModelTier.High;

    /// <summary>
    /// Hard wall-clock cap on a council run before cancellation. Leaves room for pre-research
    /// (<see cref="PreResearchTimeoutSeconds"/>), the persona fan-out and critique
    /// (<see cref="PerPersonaTimeoutSeconds"/> each) and synthesis, all on High.
    /// </summary>
    public int OverallTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Per-persona soft timeout, for a view and again for its critique. On expiry the persona view
    /// becomes "(timed out)" and synthesis proceeds without it. Room for a High-tier view that
    /// makes a research call (<see cref="ResearchAgentTimeoutSeconds"/>) and then answers.
    /// </summary>
    public int PerPersonaTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Timeout for the pre-research call. Longer than a persona's research call: it is one broad
    /// question for every lens, and nothing else runs while it does.
    /// </summary>
    public int PreResearchTimeoutSeconds { get; set; } = 150;

    /// <summary>
    /// Timeout for one research call a persona makes (<see cref="Tools.ResearchMcpClient"/>). On
    /// expiry the research task is cancelled on the server and the caller gets
    /// <c>(research timed out)</c>. Kept under <see cref="PerPersonaTimeoutSeconds"/> so a
    /// persona still has time to answer.
    /// </summary>
    public int ResearchAgentTimeoutSeconds { get; set; } = 110;

    /// <summary>
    /// The research depth the council asks for: <c>quick</c> (the default) fits a persona's budget;
    /// <c>thorough</c> runs take minutes (one measured 7.5 min) and would time out.
    /// </summary>
    public string ResearchDepth { get; set; } = "quick";

    /// <summary>The research server's MCP endpoint (stateless streamable HTTP with Tasks).</summary>
    public string ResearchServerUrl { get; set; } = "http://research-agent.rockbot.svc.cluster.local:8080/mcp";

    /// <summary>
    /// Maximum number of research tool calls a single persona may make during its view step.
    /// Past this cap, the scoped research tool short-circuits with a budget-exhausted sentinel
    /// so the persona answers from existing context.
    /// </summary>
    public int MaxResearchCallsPerPersona { get; set; } = 3;

    /// <summary>
    /// How many councils one process deliberates on at once; more wait their turn. A council fans
    /// out to pre-research plus a research call per persona, and the research server runs six at
    /// a time (<c>Research:MaxConcurrentRuns</c>), so two councils already fill it. Raise both together.
    /// </summary>
    public int MaxConcurrentRuns { get; set; } = 2;

    /// <summary>
    /// How long a clarifying question's <c>requestState</c> stays valid (plain MRTR callers). An
    /// answer that comes back later runs Select again, without asking.
    /// </summary>
    public TimeSpan ClarificationTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);
}
