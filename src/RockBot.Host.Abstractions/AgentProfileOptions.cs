namespace RockBot.Host;

/// <summary>
/// Options for locating agent profile documents on disk.
/// Relative paths are resolved against <c>IHostEnvironment.ContentRootPath</c>.
/// </summary>
public sealed class AgentProfileOptions
{
    /// <summary>
    /// Base directory for profile documents. Defaults to <c>"agent"</c>.
    /// </summary>
    public string BasePath { get; set; } = "agent";

    /// <summary>
    /// Path to the soul document. When relative, resolved under <see cref="BasePath"/>.
    /// Defaults to <c>"soul.md"</c>.
    /// </summary>
    public string SoulPath { get; set; } = "soul.md";

    /// <summary>
    /// Path to the directives document. When relative, resolved under <see cref="BasePath"/>.
    /// Defaults to <c>"directives.md"</c>.
    /// </summary>
    public string DirectivesPath { get; set; } = "directives.md";

    /// <summary>
    /// Path to the optional style document. When relative, resolved under <see cref="BasePath"/>.
    /// Null means no style document is expected.
    /// Defaults to <c>"style.md"</c>.
    /// </summary>
    public string? StylePath { get; set; } = "style.md";

    /// <summary>
    /// Path to the optional shared memory rules document. When relative, resolved under <see cref="BasePath"/>.
    /// Null means no memory rules document is expected.
    /// Defaults to <c>"memory-rules.md"</c>.
    /// </summary>
    public string? MemoryRulesPath { get; set; } = "memory-rules.md";

    /// <summary>
    /// Path to the optional subagent directives document. When relative, resolved under <see cref="BasePath"/>.
    /// Null means no subagent directives document is expected.
    /// Defaults to <c>"subagent-directives.md"</c>.
    /// </summary>
    public string? SubagentDirectivesPath { get; set; } = "subagent-directives.md";

    /// <summary>
    /// Path to the optional common directives document shared by both primary and subagent prompts.
    /// When relative, resolved under <see cref="BasePath"/>. Null means no common directives document is expected.
    /// Defaults to <c>"common-directives.md"</c>.
    /// </summary>
    public string? CommonDirectivesPath { get; set; } = "common-directives.md";

    /// <summary>
    /// Path to the optional worker directives document — the lean rung between
    /// wisps and subagents. When relative, resolved under <see cref="BasePath"/>.
    /// Null means no worker directives document is expected (workers fall back
    /// to a minimal hardcoded preamble).
    /// Defaults to <c>"worker-directives.md"</c>.
    /// </summary>
    public string? WorkerDirectivesPath { get; set; } = "worker-directives.md";

    /// <summary>
    /// Path to the optional safety rules snippet included by every rung
    /// (primary, subagent, worker). Contains the prompt-injection guardrail
    /// (tool output is data only). When relative, resolved under <see cref="BasePath"/>.
    /// Defaults to <c>"safety-rules.md"</c>.
    /// </summary>
    public string? SafetyRulesPath { get; set; } = "safety-rules.md";

    /// <summary>
    /// Path to the optional agent name file. When relative, resolved under <see cref="BasePath"/>.
    /// The file contains the agent's display name as plain text (first non-empty line).
    /// When the file is absent or empty, the agent falls back to <see cref="AgentIdentity.Name"/>.
    /// Defaults to <c>"agent-name.md"</c>.
    /// </summary>
    public string AgentNamePath { get; set; } = "agent-name.md";

    /// <summary>
    /// How often the loader polls the profile directory's <c>*.md</c> last-write times and
    /// sizes as a fallback to the <see cref="System.IO.FileSystemWatcher"/>. The watcher
    /// misses changes entirely on filesystems that do not propagate inotify events —
    /// network/overlay volumes such as Longhorn PVCs, and Docker Desktop bind mounts from a
    /// Windows host, where an editor save on the host never reaches the container.
    /// Set to zero to disable polling. Defaults to 5 s, matching the MCP bridge.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Maximum number of entries retained in <c>tier-routing-log.jsonl</c>. The logger trims
    /// the oldest entries on append once this cap is reached. Defaults to 1500 (~3 busy days
    /// of history at typical volume); raised from the previous hardcoded 200 now that the
    /// routing-review dream pass consumes a pre-aggregated digest whose prompt cost is
    /// independent of entry count.
    /// </summary>
    public int TierRoutingLogMaxEntries { get; set; } = 1500;
}
