using System.Text.Json;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// Executes the <c>get_scheduled_task_cost</c> tool: returns the cumulative LLM token/cost
/// totals recorded for one scheduled task (see <see cref="IScheduledTaskStore.RecordRunAsync"/>),
/// so the user can see what a recurring task is actually costing without grepping logs.
/// </summary>
internal sealed class GetScheduledTaskCostExecutor(ISchedulerService scheduler) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        string name;
        try
        {
            var args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments) ?? [];
            if (!args.TryGetValue("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                return Error(request, "Missing required argument: name");
            name = nameEl.GetString()!;
        }
        catch (Exception ex)
        {
            return Error(request, $"Invalid arguments: {ex.Message}");
        }

        var tasks = await scheduler.ListAsync(ct);
        var task = tasks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (task is null)
            return Error(request, $"No scheduled task named '{name}'.");

        if (task.RunCount == 0)
        {
            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = $"'{task.Name}' has not recorded any runs yet (either it has never fired, or every fire so far produced no measurable LLM usage).",
                IsError = false
            };
        }

        var avgCost = task.TotalCostUsd / task.RunCount;
        var avgInput = task.TotalInputTokens / (double)task.RunCount;
        var avgOutput = task.TotalOutputTokens / (double)task.RunCount;
        var content =
            $"**{task.Name}** — {task.RunCount} recorded run(s)\n" +
            $"  Total cost: ${task.TotalCostUsd:F4}\n" +
            $"  Average cost/run: ${avgCost:F4}\n" +
            $"  Total tokens: {task.TotalInputTokens:N0} in / {task.TotalOutputTokens:N0} out\n" +
            $"  Average tokens/run: {avgInput:N0} in / {avgOutput:N0} out";

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content,
            IsError = false
        };
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}
