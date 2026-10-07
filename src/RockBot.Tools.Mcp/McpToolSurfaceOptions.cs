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
    Eager
}

/// <summary>
/// The model-facing MCP tool surface. Bound from the <c>McpBridge</c> configuration section, so
/// <c>McpBridge:WrapperMode</c> (env <c>McpBridge__WrapperMode</c>) selects the mode.
/// </summary>
public sealed class McpToolSurfaceOptions
{
    /// <summary>Default <see cref="McpWrapperMode.Off"/> until per-tier defaults are measured (#613).</summary>
    public McpWrapperMode WrapperMode { get; set; } = McpWrapperMode.Off;
}
