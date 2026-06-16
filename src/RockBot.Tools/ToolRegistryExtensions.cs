using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

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
        Func<ToolRegistration, bool>? filter = null,
        Action<string>? onInvoke = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var tools = registry.GetTools().AsEnumerable();
        if (filter is not null) tools = tools.Where(filter);

        return tools
            .Select(r => (AIFunction)new RegistryToolFunction(
                r, registry.GetExecutor(r.Name)!, sessionId, batchId, onInvoke))
            .ToArray();
    }

    /// <summary>
    /// Materializes the registry tools permitted by <paramref name="profile"/> as
    /// <see cref="AIFunction"/> instances. Delegates to the predicate overload via
    /// <see cref="ToolProfile.Matches"/>; logs the profile and allowed/denied counts at
    /// Information so the scoped surface is observable in logs. See
    /// <see cref="BuildAgentToolFunctions(IToolRegistry, string?, string?, Func{ToolRegistration, bool}?, Action{string}?)"/>
    /// for the meaning of <paramref name="batchId"/>.
    /// </summary>
    public static AIFunction[] BuildAgentToolFunctions(
        this IToolRegistry registry,
        string? sessionId,
        string? batchId,
        ToolProfile profile,
        Action<string>? onInvoke = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(profile);

        var total = registry.GetTools().Count;
        var functions = registry.BuildAgentToolFunctions(sessionId, batchId, profile.Matches, onInvoke);

        logger?.LogInformation(
            "Tool profile '{Profile}': {Allowed}/{Total} tools allowed ({Denied} denied)",
            profile.Name, functions.Length, total, total - functions.Length);

        return functions;
    }
}
