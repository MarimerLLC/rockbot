using System.Text.Json;
using RockBot.Tools;

namespace RockBot.Subagent;

internal sealed class SpawnSubagentExecutor(ISubagentManager manager) : IToolExecutor
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

        if (!args.TryGetValue("description", out var descEl) || descEl.ValueKind != JsonValueKind.String)
            return Error(request, "Missing required argument: description");

        var description = descEl.GetString()!;
        var context = args.TryGetValue("context", out var ctxEl) ? ctxEl.GetString() : null;
        int? timeoutMinutes = args.TryGetValue("timeout_minutes", out var toEl) && toEl.TryGetInt32(out var to) ? to : null;

        var primarySessionId = request.SessionId ?? "unknown";

        var taskId = await manager.SpawnAsync(description, context, timeoutMinutes, primarySessionId, ct);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = taskId.StartsWith("Error:")
                ? taskId
                : $"Subagent spawned with task_id: {taskId}. It will report progress and send a final result when complete.",
            IsError = taskId.StartsWith("Error:")
        };
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}
