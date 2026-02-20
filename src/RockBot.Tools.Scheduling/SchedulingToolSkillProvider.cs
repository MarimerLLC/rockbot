using RockBot.Tools;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// Provides the agent with a usage guide for the scheduling tools.
/// Registered automatically when <c>AddSchedulingTools()</c> is called.
/// </summary>
internal sealed class SchedulingToolSkillProvider : IToolSkillProvider
{
    public string Name => "scheduling";
    public string Summary => "Schedule recurring tasks with cron expressions (schedule_task, cancel_scheduled_task, list_scheduled_tasks).";

    public string GetDocument() =>
        """
        # Scheduling Tools Guide

        Three tools let you create, inspect, and remove recurring tasks that fire
        automatically on a cron schedule. When a scheduled task fires, the agent wakes
        up and executes the task description as if it received a user message.


        ## Available Tools

        ### schedule_task
        Create or replace a recurring task.

        **Parameters**
        - `name` (string, required) — Unique identifier for the task. Use short,
          descriptive slugs: `daily-email-check`, `weekly-report`, `morning-briefing`.
        - `cron` (string, required) — Cron expression defining when the task fires.
          See **Cron Expression Format** below.
        - `description` (string, required) — What the agent should do when this task
          fires. Write it as a clear instruction: "Check my inbox and summarise any
          unread emails from the past 24 hours."

        ```
        schedule_task(
          name: "daily-email-check",
          cron: "0 8 * * 1-5",
          description: "Check my inbox and summarise any unread emails from the past 24 hours."
        )
        ```

        If a task with the same name already exists it is replaced — useful for
        updating the schedule or description without cancelling first.


        ### cancel_scheduled_task
        Remove a scheduled task so it no longer fires.

        **Parameters**
        - `name` (string, required) — The name of the task to cancel.

        ```
        cancel_scheduled_task(name: "daily-email-check")
        ```

        Returns a confirmation if found, or a "not found" message if the name
        does not match any scheduled task.


        ### list_scheduled_tasks
        Show all currently active scheduled tasks with their cron expressions,
        descriptions, creation time, and last-fired time.

        ```
        list_scheduled_tasks()
        ```

        No parameters required.


        ## Cron Expression Format

        Schedules use **standard 5-field cron** syntax:

        ```
        ┌───────────── minute (0–59)
        │ ┌─────────── hour (0–23)
        │ │ ┌───────── day of month (1–31)
        │ │ │ ┌─────── month (1–12 or JAN–DEC)
        │ │ │ │ ┌───── day of week (0–6, Sun=0, or SUN–SAT)
        │ │ │ │ │
        * * * * *
        ```

        Timing is evaluated in the agent's configured timezone.

        ### Common Patterns

        | Expression       | Meaning                        |
        |------------------|--------------------------------|
        | `0 8 * * 1-5`   | Weekdays at 8:00 AM            |
        | `0 9 * * *`     | Every day at 9:00 AM           |
        | `0 17 * * 5`    | Fridays at 5:00 PM             |
        | `0 12 1 * *`    | 1st of each month at noon      |
        | `*/15 * * * *`  | Every 15 minutes               |
        | `0 0 * * 0`     | Sundays at midnight            |
        | `0 8,12,17 * * 1-5` | Weekdays at 8 AM, noon, and 5 PM |

        ### Special Values

        - `*` — every value
        - `*/n` — every nth value (e.g. `*/5` every 5 minutes)
        - `n-m` — range (e.g. `1-5` Monday through Friday)
        - `n,m` — list (e.g. `8,12,17` at hours 8, 12, and 17)


        ## When to Use Scheduling

        Use scheduled tasks when the user asks to:
        - **Check something regularly** — "remind me to review my calendar every morning"
        - **Run a report on a schedule** — "send me a weekly summary of my tasks every Friday"
        - **Monitor or alert** — "check my email hourly and alert me if anything urgent arrives"
        - **Automate a routine** — "every Monday, draft a status update for my team"

        Do NOT use scheduling for one-time future events — those are better handled by
        setting a reminder directly or relying on calendar integration.


        ## Task Descriptions — Best Practices

        The description becomes the agent's instruction when the task fires. Write it
        as a clear, self-contained action:

        **Good:**
        - "Check my inbox for unread emails from the past 24 hours and summarise them."
        - "Search for the latest news about AI and write a brief digest."
        - "Review my open GitHub issues and flag any that have been idle for more than 7 days."

        **Too vague:**
        - "Check email" (what should I do with it?)
        - "Do morning tasks" (which tasks?)

        The more specific the description, the better the agent can act autonomously.


        ## Persistence

        Scheduled tasks survive agent restarts. Tasks are stored on disk and reloaded
        automatically when the agent starts. If the agent was offline when a task was
        supposed to fire, the task will fire at its next scheduled occurrence — it does
        not back-fill missed runs.


        ## Workflow Example

        User: "Remind me to check my calendar every weekday morning at 8."

        ```
        schedule_task(
          name: "morning-calendar-check",
          cron: "0 8 * * 1-5",
          description: "Check my calendar for today's events and summarise what's coming up."
        )
        ```

        Confirm with:
        ```
        list_scheduled_tasks()
        ```

        Later, to remove it:
        ```
        cancel_scheduled_task(name: "morning-calendar-check")
        ```
        """;
}
