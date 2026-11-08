using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// Executes the <c>get_dream_service_cost</c> tool: reports the cumulative LLM token/cost
/// totals DreamService's background passes have run up (see <see cref="DreamCostTracker"/>),
/// which otherwise have zero visibility — DreamService is not a scheduled task, so none of
/// its spend shows up in <c>list_scheduled_tasks</c> / <c>get_scheduled_task_cost</c>.
/// </summary>
internal sealed class GetDreamServiceCostExecutor(string costFilePath, ILogger logger) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var tracker = await DreamCostTracker.LoadAsync(costFilePath, logger);
        var lifetime = tracker.Lifetime;

        if (lifetime.TotalCycles == 0)
        {
            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = "DreamService has not completed a dream cycle yet — no cost recorded.",
                IsError = false
            };
        }

        var avgCostPerCycle = lifetime.TotalCostUsd / lifetime.TotalCycles;
        var content =
            $"**DreamService** — {lifetime.TotalCycles} cycle(s), {lifetime.TotalPassRuns} LLM call(s)\n" +
            $"  Total cost: ${lifetime.TotalCostUsd:F4}\n" +
            $"  Average cost/cycle: ${avgCostPerCycle:F4}\n" +
            $"  Total tokens: {lifetime.TotalInputTokens:N0} in / {lifetime.TotalOutputTokens:N0} out " +
            $"({lifetime.TotalCachedInputTokens:N0} cached)\n" +
            $"  Last cycle ({lifetime.LastCycleAt:u}): ${lifetime.LastCycleCostUsd:F4}, " +
            $"{lifetime.LastCyclePassRuns} call(s), {lifetime.LastCycleInputTokens:N0} in / " +
            $"{lifetime.LastCycleOutputTokens:N0} out ({lifetime.LastCycleCachedInputTokens:N0} cached)";

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content,
            IsError = false
        };
    }
}
