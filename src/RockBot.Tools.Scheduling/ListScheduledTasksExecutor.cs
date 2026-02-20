using System.Text;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// Executes the <c>list_scheduled_tasks</c> tool: returns all currently scheduled tasks.
/// </summary>
internal sealed class ListScheduledTasksExecutor(ISchedulerService scheduler) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var tasks = await scheduler.ListAsync(ct);

            string content;
            if (tasks.Count == 0)
            {
                content = "No scheduled tasks.";
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Scheduled tasks ({tasks.Count}):");
                sb.AppendLine();
                foreach (var task in tasks)
                {
                    sb.AppendLine($"**{task.Name}**");
                    sb.AppendLine($"  Cron: `{task.CronExpression}`");
                    sb.AppendLine($"  Description: {task.Description}");
                    sb.AppendLine($"  Created: {task.CreatedAt:u}");
                    if (task.LastFiredAt.HasValue)
                        sb.AppendLine($"  Last fired: {task.LastFiredAt.Value:u}");
                    sb.AppendLine();
                }
                content = sb.ToString().TrimEnd();
            }

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
            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = $"Failed to list tasks: {ex.Message}",
                IsError = true
            };
        }
    }
}
