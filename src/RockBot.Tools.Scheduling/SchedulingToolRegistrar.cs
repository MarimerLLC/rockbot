using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// Hosted service that registers the scheduling tools with the tool registry at startup.
/// </summary>
internal sealed class SchedulingToolRegistrar(
    IToolRegistry registry,
    ISchedulerService scheduler,
    ILogger<SchedulingToolRegistrar> logger) : IHostedService
{
    private const string ScheduleSchema = """
        {
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "description": "Unique name for the task (e.g. 'daily-email-check')"
            },
            "cron": {
              "type": "string",
              "description": "Cron expression for when the task fires. Two formats are supported: standard 5-field (minute hour day-of-month month day-of-week, e.g. '0 8 * * 1-5') or 6-field with a leading seconds field (second minute hour day-of-month month day-of-week, e.g. '0 0 9 * * *'). Use specific values for one-time tasks, wildcards for recurring. Current time is in your system prompt — use it to compute target times."
            },
            "description": {
              "type": "string",
              "description": "What the agent should do when this task fires"
            }
          },
          "required": ["name", "cron", "description"]
        }
        """;

    private const string CancelSchema = """
        {
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "description": "Name of the scheduled task to cancel"
            }
          },
          "required": ["name"]
        }
        """;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Register(new ToolRegistration
        {
            Name = "schedule_task",
            Description = "Schedule a one-time or recurring task using a cron expression. The agent will execute the description when the task fires and the response will be shown to the user. Supports 5-field (minute-resolution) and 6-field (second-resolution) cron. Use specific field values for one-time tasks.",
            ParametersSchema = ScheduleSchema,
            Source = "scheduling"
        }, new ScheduleTaskExecutor(scheduler));
        logger.LogInformation("Registered scheduling tool: schedule_task");

        registry.Register(new ToolRegistration
        {
            Name = "cancel_scheduled_task",
            Description = "Cancel and remove a scheduled task by name.",
            ParametersSchema = CancelSchema,
            Source = "scheduling"
        }, new CancelScheduledTaskExecutor(scheduler));
        logger.LogInformation("Registered scheduling tool: cancel_scheduled_task");

        registry.Register(new ToolRegistration
        {
            Name = "list_scheduled_tasks",
            Description = "List all currently scheduled tasks with their cron expressions and descriptions.",
            ParametersSchema = null,
            Source = "scheduling"
        }, new ListScheduledTasksExecutor(scheduler));
        logger.LogInformation("Registered scheduling tool: list_scheduled_tasks");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
