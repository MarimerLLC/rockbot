using System.Text.Json;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// Executes the <c>cancel_scheduled_task</c> tool: removes a scheduled task by name.
/// </summary>
internal sealed class CancelScheduledTaskExecutor(ISchedulerService scheduler) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        string name;
        try
        {
            var args = ParseArgs(request.Arguments);
            if (!args.TryGetValue("name", out var el))
                return Error(request, "Missing required argument: name");
            name = el.GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return Error(request, $"Invalid arguments: {ex.Message}");
        }

        try
        {
            var cancelled = await scheduler.CancelAsync(name, ct);
            var content = cancelled
                ? $"Cancelled scheduled task '{name}'."
                : $"No scheduled task named '{name}' was found.";

            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = content,
                IsError = false
            };
        }
        catch (Exception ex)
        {
            return Error(request, $"Failed to cancel task: {ex.Message}");
        }
    }

    private static Dictionary<string, JsonElement> ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest request, string message) =>
        new() { ToolCallId = request.ToolCallId, ToolName = request.ToolName, Content = message, IsError = true };
}
