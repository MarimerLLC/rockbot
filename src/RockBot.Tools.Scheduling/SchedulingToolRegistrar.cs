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
              "description": "Cron expression. 5-field: 'minute hour day month dow' (e.g. '0 8 * * 1-5'). 6-field with seconds: 'second minute hour day month dow' (e.g. '15 23 14 5 3 *'). For one-time tasks pin every field to the exact target moment — read the current time WITH SECONDS from your system prompt, add the offset, and pin each field. Always use * for day-of-week on one-time tasks."
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
            Description = """
                Schedule a one-time or recurring task. When the task fires, the agent executes the
                description and the response is shown to the user.

                TWO CRON FORMATS:
                  5-field (minute resolution): minute hour day month day-of-week
                    Example recurring: "0 8 * * 1-5" = weekdays at 8 AM
                  6-field (second resolution): second minute hour day month day-of-week
                    Example recurring: "*/10 * * * * *" = every 10 seconds

                ONE-TIME TASKS — pin all fields to the exact target moment.
                Your system prompt contains the CURRENT date and time including seconds.
                Add the requested offset to get the target, then pin each field.

                Example — user says "say hello in 30 seconds", current time is 14:22:45 on March 5:
                  Target = 14:23:15 → 6-field cron: "15 23 14 5 3 *"
                  (second=15, minute=23, hour=14, day=5, month=3, day-of-week=*)

                Example — user says "remind me in 2 minutes", current time is 14:22 on March 5:
                  Target = 14:24 → 5-field cron: "24 14 5 3 *"
                  (minute=24, hour=14, day=5, month=3, day-of-week=*)

                Always set day-of-week to * for one-time tasks to avoid AND-logic issues.
                """,
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
