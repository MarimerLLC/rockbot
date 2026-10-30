using System.Text.Json;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// Executes the <c>set_task_time_zone</c> tool (#740): changes only the zone an existing task's
/// cron is read in. Everything else about the task — cron, description, output format, directive,
/// last-fired time — is kept, which re-scheduling it with <c>schedule_task</c> would not do.
/// Registered only when the agent follows the user's location.
/// </summary>
internal sealed class SetTaskTimeZoneExecutor(ISchedulerService scheduler, AgentClock clock) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        string? name, spec;
        try
        {
            var args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments) ?? [];
            name = args.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            spec = args.TryGetValue("timeZone", out var z) && z.ValueKind == JsonValueKind.String ? z.GetString() : null;
        }
        catch (JsonException ex)
        {
            return Response(request, $"Invalid arguments: {ex.Message}", isError: true);
        }

        if (string.IsNullOrWhiteSpace(name))
            return Response(request, "Missing required argument: name", isError: true);

        try
        {
            var tasks = await scheduler.ListAsync(ct);
            var task = tasks.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (task is null)
            {
                var known = tasks.Count == 0 ? "none" : string.Join(", ", tasks.Select(t => t.Name));
                return Response(request, $"No scheduled task named '{name}'. Scheduled tasks: {known}.", isError: true);
            }

            string timeZone;
            try
            {
                timeZone = ScheduleTaskExecutor.NormalizeTimeZone(spec, task.RunOnce, clock);
            }
            catch (ArgumentException ex)
            {
                return Response(request, ex.Message, isError: true);
            }

            var updated = task with { TimeZone = timeZone };
            await scheduler.ScheduleAsync(updated, ct);

            var zone = clock.ResolveZone(timeZone);
            var next = scheduler.GetNextOccurrence(updated);
            var nextText = next.HasValue
                ? $"Next fire: {TimeZoneInfo.ConvertTime(next.Value, zone):yyyy-MM-dd HH:mm:ss} ({zone.Id})."
                : "WARNING: no future occurrence found — task will never fire.";
            return Response(request,
                $"Task '{task.Name}' now runs in {ScheduleTaskExecutor.DescribeZone(timeZone, zone)}" +
                $" (was {ScheduleTaskExecutor.DescribeZone(task.TimeZone, clock.ResolveZone(task.TimeZone))})." +
                $" Its cron '{task.CronExpression}' is unchanged and is now read in that zone. {nextText}" +
                " Description, output format and directive are unchanged.",
                isError: false);
        }
        catch (Exception ex)
        {
            return Response(request, $"Failed to change the task's time zone: {ex.Message}", isError: true);
        }
    }

    private static ToolInvokeResponse Response(ToolInvokeRequest request, string content, bool isError) => new()
    {
        ToolCallId = request.ToolCallId,
        ToolName = request.ToolName,
        Content = content,
        IsError = isError
    };
}
