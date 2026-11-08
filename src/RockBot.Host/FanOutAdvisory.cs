namespace RockBot.Host;

/// <summary>
/// Cheap, static nudge for scheduled-task text that asks for subagent/worker fan-out. That pattern
/// re-ships full context (system prompt, tool schemas, memory) on every turn of every spawned unit,
/// and has historically cost 5-20x what the same gather-heavy work costs running directly in the
/// primary loop — see heartbeat-patrol's own live directive, which evolved away from fan-out for
/// exactly this reason. This is not a dynamic cost projection (TierRoutingAnalyzer needs historical
/// routing entries to project cost, which a brand-new description has none of yet) — just a pointer
/// at the cheaper default, surfaced once at write time so the mistake is easy to avoid instead of
/// easy to make.
/// </summary>
public static class FanOutAdvisory
{
    private static readonly string[] FanOutTerms =
    [
        "spawn_subagent", "spawn_workers", "subagent", "subagents", "worker fan-out", "fan-out", "fan out",
    ];

    /// <summary>True when <paramref name="text"/> asks for subagent/worker fan-out.</summary>
    public static bool MentionsFanOut(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        FanOutTerms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>Short advisory to append to a tool's confirmation text when <see cref="MentionsFanOut"/> is true.</summary>
    public const string Message =
        "Note: this description asks for subagent/worker fan-out. Every spawned subagent/worker " +
        "re-ships its full context (system prompt, tool schemas, memory) on every one of its turns, " +
        "which has historically cost 5-20x what the same gather-heavy work costs running directly in " +
        "the primary loop. Unless this task genuinely needs its own isolated multi-turn loop, consider " +
        "describing it to run directly in the primary loop instead — see heartbeat-patrol's live " +
        "directive for a working example. Check actual cost later with get_scheduled_task_cost.";
}
