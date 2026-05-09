namespace RockBot.Host;

/// <summary>
/// Options for the closed-loop repair-ticket pipeline. When <see cref="BasePath"/>
/// is relative it is resolved under <see cref="AgentProfileOptions.BasePath"/>,
/// matching <see cref="FailureClusterOptions"/>.
/// See <c>design/self-repair.md</c> Phase 4.
/// </summary>
public sealed class RepairTicketOptions
{
    /// <summary>
    /// Whether the closed-loop repair-ticket passes (creation + apply) run during
    /// each dream cycle. Default true. The store and appliers are still registered
    /// when false so a future cycle can pick them up without restart.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base directory for ticket JSON files. Defaults to <c>"repair-tickets"</c>.
    /// When relative, resolved under the agent profile base path
    /// (<c>/data/agent/repair-tickets</c> in K8s).
    /// </summary>
    public string BasePath { get; set; } = "repair-tickets";

    /// <summary>
    /// Maximum number of failed verify attempts before a ticket is escalated.
    /// Default 3. Uncertain verifies (gateway error, budget exceeded) do not count.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Maximum number of new tickets the LLM-driven creation pass may open in a
    /// single dream cycle. Default 5. Bounds the blast radius of a bad LLM cycle.
    /// </summary>
    public int MaxTicketsPerCycle { get; set; } = 5;

    /// <summary>
    /// Working-memory key under which the apply pass writes the rolling escalation
    /// summary. Default <c>repair-escalations-latest</c>. Overwritten each cycle that
    /// produces an escalated ticket.
    /// </summary>
    public string EscalationWmKey { get; set; } = "repair-escalations-latest";

    /// <summary>
    /// TTL for the escalation working-memory entry. Default 7 days — long enough
    /// for the user to see the escalation across multiple sessions, short enough
    /// that stale entries self-purge.
    /// </summary>
    public TimeSpan EscalationWmTtl { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Path to the repair-ticket creation directive file, relative to
    /// <see cref="AgentProfileOptions.BasePath"/>. When the file does not exist,
    /// a built-in fallback directive is used.
    /// </summary>
    public string CreationDirectivePath { get; set; } = "repair-ticket-creation.md";
}
