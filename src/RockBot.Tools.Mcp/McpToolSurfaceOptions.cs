using RockBot.Host;

namespace RockBot.Tools.Mcp;

/// <summary>How downstream MCP tools are offered to the model.</summary>
public enum McpWrapperMode
{
    /// <summary>Only the <c>mcp_*</c> management tools; every call goes through <c>mcp_invoke_tool</c>.</summary>
    Off,

    /// <summary>
    /// Every downstream tool is also registered as a typed <c>{server}__{tool}</c> tool whose
    /// parameter schema is the downstream input schema, unchanged. <c>mcp_invoke_tool</c> stays
    /// available as the escape hatch.
    /// </summary>
    Eager,

    /// <summary>
    /// The same typed tools, but none are in the baseline tool list. <c>mcp_find_tools</c>,
    /// <c>mcp_get_service_details</c> and a call by typed name activate them for the calling
    /// session only, and they become callable on the loop's next iteration (#612).
    /// </summary>
    Lazy,

    /// <summary>
    /// <see cref="Lazy"/>, and once a session has called one of a server's tools (by either path),
    /// every typed tool of that server stays in the session's tool list (#613).
    /// </summary>
    Pinned
}

/// <summary>
/// The model-facing MCP tool surface. Bound from the <c>McpBridge</c> configuration section, so
/// <c>McpBridge:WrapperMode</c> (env <c>McpBridge__WrapperMode</c>) selects the mode, and
/// <c>McpBridge:WrapperModeByTier:{Low|Balanced|High}</c> overrides it for one model tier (#613).
/// </summary>
public sealed class McpToolSurfaceOptions
{
    /// <summary>The mode of every tier <see cref="WrapperModeByTier"/> doesn't name.</summary>
    public McpWrapperMode WrapperMode { get; set; } = McpWrapperMode.Off;

    /// <summary>Per-tier overrides of <see cref="WrapperMode"/>.</summary>
    public Dictionary<ModelTier, McpWrapperMode> WrapperModeByTier { get; set; } = [];

    /// <summary>
    /// Pinned mode: the most servers one session keeps pinned. Calling another unpins the
    /// session's least recently called server.
    /// </summary>
    public int MaxPinnedServersPerSession { get; set; } = 3;

    /// <summary>
    /// Lazy and pinned modes: the most typed tools one session keeps activated. Activating another drops the
    /// session's oldest activation.
    /// </summary>
    public int MaxActivatedToolsPerSession { get; set; } = 40;

    /// <summary>
    /// Lazy and pinned modes: a session's activations and pins are dropped after this long without use.
    /// </summary>
    public TimeSpan ActivationIdleTimeout { get; set; } = TimeSpan.FromHours(12);

    /// <summary>The mode a run on <paramref name="tier"/> uses.</summary>
    public McpWrapperMode ModeFor(ModelTier tier) =>
        WrapperModeByTier.TryGetValue(tier, out var mode) ? mode : WrapperMode;

    /// <summary>Every tier's mode.</summary>
    public IEnumerable<McpWrapperMode> Modes => Enum.GetValues<ModelTier>().Select(ModeFor);

    /// <summary>Some tier offers typed tools, so they are indexed.</summary>
    public bool IndexesWrappers => Modes.Any(m => m != McpWrapperMode.Off);

    /// <summary>Some tier runs eager, so typed tools are registered in the tool registry.</summary>
    public bool RegistersWrappers => Modes.Any(m => m == McpWrapperMode.Eager);

    /// <summary>Some tier activates typed tools per session, so <c>mcp_find_tools</c> is registered.</summary>
    public bool ActivatesWrappers => Modes.Any(m => m is McpWrapperMode.Lazy or McpWrapperMode.Pinned);

    /// <summary>Some tier pins called servers.</summary>
    public bool PinsServers => Modes.Any(m => m == McpWrapperMode.Pinned);

    /// <summary>The modes, by tier, for logs.</summary>
    public string Describe() =>
        string.Join(", ", Enum.GetValues<ModelTier>().Select(t => $"{t}={ModeFor(t)}"));
}
