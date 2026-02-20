using System.Text;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// Executes the <c>list_scheduled_tasks</c> tool: returns all currently scheduled tasks.
/// </summary>
internal sealed class ListScheduledTasksExecutor(ISchedulerService scheduler, AgentClock clock) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var now = clock.Now;
            var tasks = await scheduler.ListAsync(ct);

            string content;
            if (tasks.Count == 0)
            {
                content = $"No scheduled tasks. Current time: {now:yyyy-MM-dd HH:mm:ss} ({clock.Zone.Id})";
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Current time: {now:yyyy-MM-dd HH:mm:ss} ({clock.Zone.Id})");
                sb.AppendLine($"Scheduled tasks ({tasks.Count}):");
                sb.AppendLine();
                foreach (var task in tasks)
                {
                    var next = scheduler.GetNextOccurrence(task);
                    sb.AppendLine($"**{task.Name}**");
                    sb.AppendLine($"  Cron: `{task.CronExpression}`");
                    if (next.HasValue)
                        sb.AppendLine($"  Next fire: {next.Value:yyyy-MM-dd HH:mm:ss} ({clock.Zone.Id}){(next.Value < now ? " ⚠️ OVERDUE" : "")}");
                    else
                        sb.AppendLine($"  Next fire: none (cron has no future occurrence — task will be removed)");
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
