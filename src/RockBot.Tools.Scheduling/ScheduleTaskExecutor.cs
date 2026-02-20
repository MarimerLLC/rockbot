using System.Text.Json;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// Executes the <c>schedule_task</c> tool: creates or replaces a cron-scheduled task.
/// </summary>
internal sealed class ScheduleTaskExecutor(ISchedulerService scheduler) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        string name, cron, description;
        try
        {
            var args = ParseArgs(request.Arguments);
            name = GetRequired(args, "name");
            cron = GetRequired(args, "cron");
            description = GetRequired(args, "description");
        }
        catch (Exception ex)
        {
            return Error(request, ex.Message);
        }

        try
        {
            var task = new ScheduledTask(
                Name: name,
                CronExpression: cron,
                Description: description,
                CreatedAt: DateTimeOffset.UtcNow);

            await scheduler.ScheduleAsync(task, ct);

            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = $"Scheduled task '{name}' with cron '{cron}'.",
                IsError = false
            };
        }
        catch (Exception ex)
        {
            return Error(request, $"Failed to schedule task: {ex.Message}");
        }
    }

    private static Dictionary<string, JsonElement> ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
    }

    private static string GetRequired(Dictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var el))
            throw new ArgumentException($"Missing required argument: {key}");
        return el.GetString() ?? throw new ArgumentException($"Argument '{key}' must be a non-null string");
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest request, string message) =>
        new() { ToolCallId = request.ToolCallId, ToolName = request.ToolName, Content = message, IsError = true };
}
