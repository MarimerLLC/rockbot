using Microsoft.Extensions.AI;

namespace RockBot.Tools;

/// <summary>
/// Helpers for materializing registry tools as <see cref="AIFunction"/> instances
/// for an agent loop invocation.
/// </summary>
public static class ToolRegistryExtensions
{
    /// <summary>
    /// Wraps every registered tool (optionally filtered) as an <see cref="AIFunction"/>
    /// scoped to a single agent loop invocation. The <paramref name="batchId"/> is
    /// required so that any <c>spawn_subagent</c> calls made during this loop share
    /// a batch and produce a single consolidated synthesis when their results return,
    /// rather than firing one synthesis per subagent completion. Pass null only for
    /// invocations that genuinely cannot spawn subagents.
    /// </summary>
    public static AIFunction[] BuildAgentToolFunctions(
        this IToolRegistry registry,
        string? sessionId,
        string? batchId,
        Func<ToolRegistration, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var tools = registry.GetTools().AsEnumerable();
        if (filter is not null) tools = tools.Where(filter);

        return tools
            .Select(r => (AIFunction)new RegistryToolFunction(
                r, registry.GetExecutor(r.Name)!, sessionId, batchId))
            .ToArray();
    }
}
