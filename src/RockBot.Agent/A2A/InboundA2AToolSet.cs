using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Memory;

namespace RockBot.Agent.A2A;

/// <summary>
/// Assembles a restricted tool set for inbound A2A task processing at Level 1 (Observe).
/// Only read-oriented tools are exposed: working memory read/list/search, long-term memory
/// search, and a scoped working memory write limited to the a2a-inbox namespace.
/// </summary>
internal static class InboundA2AToolSet
{
    /// <summary>
    /// Builds the restricted tool list for an inbound A2A task.
    /// </summary>
    /// <param name="workingMemory">Global working memory instance.</param>
    /// <param name="memoryTools">Long-term memory tools (only SearchMemory is included).</param>
    /// <param name="taskId">The A2A task ID — used as the working memory namespace.</param>
    /// <param name="logger">Logger for tool invocations.</param>
    public static IList<AITool> Build(
        IWorkingMemory workingMemory,
        MemoryTools memoryTools,
        string taskId,
        ILogger logger)
    {
        // Working memory scoped to a2a-inbox/{taskId} — writes are contained to this namespace
        var wmTools = new WorkingMemoryTools(workingMemory, $"a2a-inbox/{taskId}", logger);

        // From working memory tools: include all (read + write scoped to inbox namespace)
        var tools = new List<AITool>(wmTools.Tools);

        // From long-term memory: include only SearchMemory (read-only)
        var searchMemory = memoryTools.Tools
            .OfType<AIFunction>()
            .FirstOrDefault(f => f.Name == "SearchMemory");
        if (searchMemory is not null)
            tools.Add(searchMemory);

        return tools;
    }
}
