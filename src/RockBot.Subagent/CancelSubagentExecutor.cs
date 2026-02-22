using System.Text.Json;
using RockBot.Tools;

namespace RockBot.Subagent;

internal sealed class CancelSubagentExecutor(ISubagentManager manager) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        Dictionary<string, JsonElement> args;
        try
        {
            args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments) ?? [];
        }
        catch
        {
            return Error(request, "Invalid arguments JSON");
        }

        if (!args.TryGetValue("task_id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return Error(request, "Missing required argument: task_id");

        var taskId = idEl.GetString()!;
        var cancelled = await manager.CancelAsync(taskId);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = cancelled ? $"Subagent {taskId} cancelled." : $"No active subagent found with task_id '{taskId}'.",
            IsError = false
        };
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}
