using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Resolves a wisp's MCP step to a downstream tool and, where one exists, its typed
/// <c>{server}__{tool}</c> wrapper (#647). A step names its tool either as <c>server</c> plus the
/// server's own tool name, or by the typed name alone (<c>"tool": "calendar-mcp__get_events"</c>),
/// which is looked up — never parsed.
/// </summary>
internal static class McpStepResolver
{
    /// <summary>True for a gateway-MCP step that names a tool without a server: the typed shorthand.</summary>
    public static bool IsShorthand(WispStep step) =>
        step.Gateway == GatewayType.Mcp && string.IsNullOrWhiteSpace(step.Server) && !string.IsNullOrWhiteSpace(step.Tool);

    /// <summary>
    /// The tool <paramref name="step"/> calls, or <c>null</c> when it isn't an MCP step, there's no
    /// directory, or the directory doesn't know the tool (yet).
    /// </summary>
    public static McpToolEntry? TryResolve(WispStep step, IMcpToolDirectory? directory)
    {
        if (directory is null || step.Gateway != GatewayType.Mcp || string.IsNullOrWhiteSpace(step.Tool))
            return null;

        try
        {
            return directory.Resolve(string.IsNullOrWhiteSpace(step.Server) ? null : step.Server, step.Tool);
        }
        catch
        {
            return null;
        }
    }

    /// <summary><paramref name="step"/> rewritten to the canonical server and downstream tool name.</summary>
    public static WispStep Canonicalize(WispStep step, McpToolEntry entry) =>
        step with { Server = entry.ServerName, Tool = entry.ToolName };

    /// <summary>
    /// <paramref name="definition"/> with every shorthand step the directory can resolve rewritten
    /// to <c>server</c> + <c>tool</c>, so hashes and stored bodies don't depend on which form the
    /// author wrote. Steps it can't resolve yet are left as written.
    /// </summary>
    public static WispDefinition CanonicalizeShorthand(WispDefinition definition, IMcpToolDirectory? directory)
    {
        if (directory is null || !definition.Steps.Any(IsShorthand))
            return definition;

        var changed = false;
        var steps = definition.Steps.Select(s =>
        {
            if (!IsShorthand(s) || TryResolve(s, directory) is not { } entry)
                return s;
            changed = true;
            return Canonicalize(s, entry);
        }).ToList();

        return changed ? definition with { Steps = steps } : definition;
    }
}
