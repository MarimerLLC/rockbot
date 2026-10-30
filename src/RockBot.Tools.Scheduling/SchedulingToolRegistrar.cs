using System.Text.Json.Nodes;
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
    AgentClock clock,
    ILogger<SchedulingToolRegistrar> logger,
    UserLocationState? userLocation = null) : IHostedService
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
            },
            "runOnce": {
              "type": "boolean",
              "description": "Set to true for one-time tasks (reminders, deferred actions). The task is automatically deleted after it fires. Omit or set false for recurring tasks."
            },
            "outputFormat": {
              "type": "string",
              "enum": ["plain", "markdown", "rich"],
              "description": "Rendering format the task's output is authored for. 'plain' (default) emits plain text. 'markdown' permits headings, tables, code blocks, links. 'rich' additionally permits inline HTML for color and inline SVG for charts — use only for tasks whose audience will read the output in a client that renders HTML (e.g. the Blazor UI). Persists with the schedule; ignored on CLI/text-only clients."
            }
          },
          "required": ["name", "cron", "description"]
        }
        """;

    // Added to schedule_task only when the agent follows the user's location (#740).
    private const string TimeZoneProperty = """
        {
          "type": "string",
          "description": "Which zone the cron fields are written in: 'local' (follows the user as they travel), 'home' (the user's home zone), or a fixed IANA id such as 'Europe/London'."
        }
        """;

    private const string TimeZoneGuidance = """
        TIME ZONE (required): every task is marked 'local' or 'home' (or a fixed IANA id).
          local — tied to the user's own day wherever they are: a morning brief, "remind me at dinner".
          home  — tied to home-office hours or to other people at home.
          One-time relative reminders ("in 20 minutes") use local.
        Write the cron fields in the zone you choose. When the user is away from home, your datetime
        context shows both their current zone and their home zone.
        To change only an existing task's zone, use set_task_time_zone. Calling schedule_task with
        the same name replaces the whole task: pass its current outputFormat (shown by
        list_scheduled_tasks) or it falls back to plain, and its directive is dropped.
        """;

    private const string SetTimeZoneSchema = """
        {
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "description": "Name of the existing scheduled task"
            },
            "timeZone": {
              "type": "string",
              "description": "'local' (follows the user as they travel), 'home' (the user's home zone), or a fixed IANA id such as 'Europe/London'."
            }
          },
          "required": ["name", "timeZone"]
        }
        """;

    internal const string SetTimeZoneDescription = """
        Change which zone an existing scheduled task runs in, keeping everything else about it (cron,
        description, output format, directive). The cron fields are not converted: "0 10 * * 1-5"
        moved from home to local still fires at 10:00, now 10:00 wherever the user is.
          local — tied to the user's own day wherever they are: a morning brief, an evening summary.
          home  — tied to home-office hours or to other people at home.
        list_scheduled_tasks shows each task's current zone.
        """;

    /// <summary>The <c>schedule_task</c> parameter schema, with a required <c>timeZone</c> when location is enabled.</summary>
    internal static string BuildScheduleSchema(bool locationEnabled)
    {
        if (!locationEnabled)
            return ScheduleSchema;

        var schema = JsonNode.Parse(ScheduleSchema)!.AsObject();
        schema["properties"]!.AsObject()["timeZone"] = JsonNode.Parse(TimeZoneProperty);
        schema["required"]!.AsArray().Add("timeZone");
        return schema.ToJsonString();
    }

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
        var locationEnabled = userLocation is not null;

        registry.Register(new ToolRegistration
        {
            Name = "schedule_task",
            Description = locationEnabled ? ScheduleDescription + "\n\n" + TimeZoneGuidance : ScheduleDescription,
            ParametersSchema = BuildScheduleSchema(locationEnabled),
            Source = "scheduling"
        }, new ScheduleTaskExecutor(scheduler, clock, locationEnabled));
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
        }, new ListScheduledTasksExecutor(scheduler, clock, locationEnabled));
        logger.LogInformation("Registered scheduling tool: list_scheduled_tasks");

        if (locationEnabled)
        {
            registry.Register(new ToolRegistration
            {
                Name = "set_task_time_zone",
                Description = SetTimeZoneDescription,
                ParametersSchema = SetTimeZoneSchema,
                Source = "scheduling"
            }, new SetTaskTimeZoneExecutor(scheduler, clock));
            logger.LogInformation("Registered scheduling tool: set_task_time_zone");
        }

        return Task.CompletedTask;
    }

    internal const string ScheduleDescription = """
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
                Set runOnce=true for one-time tasks so they are automatically removed after firing.
                """;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
