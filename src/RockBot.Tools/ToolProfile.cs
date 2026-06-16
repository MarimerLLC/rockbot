using System.Collections.Immutable;

namespace RockBot.Tools;

/// <summary>
/// A named, in-code allow/deny policy over the tool registry, used to scope the tool
/// surface advertised to the LLM for a particular in-process role (main turn, subagent,
/// scheduled task, A2A result synthesis).
/// </summary>
/// <remarks>
/// Profiles are intentionally <b>not</b> hot-reloadable. They are a safety boundary —
/// subagents must not register MCP servers, the A2A synthesis loop must not dispatch a
/// fresh <c>invoke_agent</c> while folding in the current result. A bad PVC edit silently
/// opening that boundary is a worse failure mode than a redeploy.
///
/// Matching fails <i>closed</i>: a registration is included only when it is not denied
/// AND it is explicitly allowed (by name, by source, or by the <c>"*"</c> source wildcard).
/// New tool sources therefore do not leak into a restricted profile automatically — they
/// must be added consciously (the snapshot drift test enforces this).
/// </remarks>
public sealed record ToolProfile(
    string Name,
    ImmutableHashSet<string> AllowedSources,
    ImmutableHashSet<string> DeniedSources,
    ImmutableHashSet<string> AllowedToolNames,
    ImmutableHashSet<string> DeniedToolNames)
{
    /// <summary>Returns true if the given registration is permitted by this profile.</summary>
    public bool Matches(ToolRegistration r) =>
        !DeniedToolNames.Contains(r.Name)
        && !DeniedSources.Contains(r.Source)
        && (AllowedToolNames.Contains(r.Name)
            || AllowedSources.Contains("*")
            || AllowedSources.Contains(r.Source));

    /// <summary>A profile matching every registered tool. The basis for composition.</summary>
    public static ToolProfile All { get; } = new(
        Name: "All",
        AllowedSources: ["*"],
        DeniedSources: [],
        AllowedToolNames: [],
        DeniedToolNames: []);

    /// <summary>Returns a copy with a different display name.</summary>
    public ToolProfile Named(string name) => this with { Name = name };

    /// <summary>Returns a copy that additionally denies the given tool <paramref name="sources"/>.</summary>
    public ToolProfile DenyingSources(params string[] sources) =>
        this with { DeniedSources = DeniedSources.Union(sources) };

    /// <summary>Returns a copy that additionally denies the given tool <paramref name="names"/>.</summary>
    public ToolProfile DenyingToolNames(params string[] names) =>
        this with { DeniedToolNames = DeniedToolNames.Union(names) };

    /// <summary>
    /// Returns a copy whose allowed surface is restricted to exactly the given
    /// <paramref name="sources"/> (replaces the <c>"*"</c> wildcard).
    /// </summary>
    public ToolProfile AllowingOnlySources(params string[] sources) =>
        this with { AllowedSources = [..sources] };

    /// <summary>Returns a copy that additionally allow-lists the given tool <paramref name="names"/>.</summary>
    public ToolProfile AllowingToolNames(params string[] names) =>
        this with { AllowedToolNames = AllowedToolNames.Union(names) };
}

/// <summary>
/// The named tool profiles applied across the in-process roles that share the main agent's
/// DI container. Each restricted profile preserves the behavior of the ad-hoc inline filter
/// it replaced.
/// </summary>
public static class ToolProfiles
{
    /// <summary>
    /// Everything. The default for the primary user-facing turn — preserves the
    /// historical unfiltered surface. Named explicitly at call sites so that future
    /// tool additions land here visibly rather than leaking into a restricted profile.
    /// </summary>
    public static ToolProfile Main { get; } = ToolProfile.All.Named("Main");

    /// <summary>
    /// Subagent surface. Mirrors the long-standing <c>SubagentRunner</c> predicate:
    /// <list type="bullet">
    ///   <item><c>subagent</c> — no spawning nested subagents.</item>
    ///   <item><c>scheduling</c> — no creating new scheduled tasks.</item>
    ///   <item><c>a2a</c> — <c>invoke_agent</c> is async and its result folds into the
    ///   primary session, not the subagent's, so it is silently useless here.</item>
    ///   <item><c>mcp_register_server</c> / <c>mcp_unregister_server</c> — infrastructure-only;
    ///   subagents must not reconfigure the MCP bridge.</item>
    /// </list>
    /// Everything else (including <c>mcp:management</c> invoke/list/details) is allowed.
    /// </summary>
    public static ToolProfile Subagent { get; } = ToolProfile.All
        .Named("Subagent")
        .DenyingSources("subagent", "scheduling", "a2a")
        .DenyingToolNames("mcp_register_server", "mcp_unregister_server");

    /// <summary>
    /// Scheduled-task surface. Denies only MCP bridge reconfiguration — scheduled tasks
    /// (e.g. the heartbeat patrol) legitimately spawn subagents, so source <c>subagent</c>
    /// is intentionally <b>not</b> denied here despite the originating issue's table:
    /// <c>ScheduledTaskHandler</c> tracks <c>spawn_subagent</c> invocations and denying the
    /// source would silently disable that path.
    /// </summary>
    public static ToolProfile Scheduled { get; } = ToolProfile.All
        .Named("Scheduled")
        .DenyingToolNames("mcp_register_server", "mcp_unregister_server");

    /// <summary>
    /// A2A result-synthesis surface. Denies the A2A caller tools so the synthesis LLM
    /// presents the current result instead of dispatching a fresh agent interaction.
    /// Mirrors the inline HashSet previously used only in <c>A2ATaskResultHandler</c>;
    /// now applied to all A2A receive-side handlers and the late-reply fold-back handler.
    /// </summary>
    public static ToolProfile A2ASynthesis { get; } = ToolProfile.All
        .Named("A2ASynthesis")
        .DenyingToolNames(
            "invoke_agent", "register_agent", "unregister_agent",
            "list_known_agents", "get_agent_details");
}
