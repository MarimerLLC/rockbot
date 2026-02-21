using System.Text;
using RockBot.Tools;

namespace RockBot.Subagent;

internal sealed class ListSubagentsExecutor(ISubagentManager manager) : IToolExecutor
{
    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var active = manager.ListActive();

        string content;
        if (active.Count == 0)
        {
            content = "No active subagents.";
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Active subagents ({active.Count}):");
            foreach (var e in active)
            {
                var elapsed = DateTimeOffset.UtcNow - e.StartedAt;
                sb.AppendLine($"- task_id={e.TaskId}, elapsed={elapsed.TotalSeconds:F0}s, description={e.Description}");
            }
            content = sb.ToString().Trim();
        }

        return Task.FromResult(new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content,
            IsError = false
        });
    }
}
