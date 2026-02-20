using RockBot.Tools;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// Provides the agent with a usage guide for the scheduling tools.
/// Registered automatically when <c>AddSchedulingTools()</c> is called.
/// </summary>
internal sealed class SchedulingToolSkillProvider : IToolSkillProvider
{
    public string Name => "scheduling";
    public string Summary => "Schedule one-time or recurring tasks with cron expressions (schedule_task, cancel_scheduled_task, list_scheduled_tasks).";

    public string GetDocument() =>
        """
        # Scheduling Tools Guide

        Three tools let you create, inspect, and remove tasks that fire automatically
        on a cron schedule. Tasks can be one-time (fire once at a specific moment) or
        recurring (fire on a repeating pattern). When a task fires, the agent executes
        the description and sends the response to the user.


        ## Available Tools

        ### schedule_task
        Create or replace a scheduled task.

        **Parameters**
        - `name` (string, required) — Unique identifier for the task. Use short,
          descriptive slugs: `daily-email-check`, `weekly-report`, `remind-at-2pm`.
        - `cron` (string, required) — Cron expression defining when the task fires.
          See **Cron Expression Format** below.
        - `description` (string, required) — What the agent should do when this task
          fires. Write it as a clear, self-contained instruction.

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

        ```
        cancel_scheduled_task(name: "daily-email-check")
        ```


        ### list_scheduled_tasks
        Show all active scheduled tasks with their cron expressions, descriptions,
        creation time, and last-fired time.

        ```
        list_scheduled_tasks()
        ```


        ## Cron Expression Format

        Two formats are supported:

        ### 5-field (minute resolution)

        ```
        ┌───────────── minute (0–59)
        │ ┌─────────── hour (0–23)
        │ │ ┌───────── day of month (1–31)
        │ │ │ ┌─────── month (1–12 or JAN–DEC)
        │ │ │ │ ┌───── day of week (0–6, Sun=0, or SUN–SAT)
        │ │ │ │ │
        * * * * *
        ```

        ### 6-field (second resolution — leading seconds field)

        ```
        ┌─────────────── second (0–59)
        │ ┌───────────── minute (0–59)
        │ │ ┌─────────── hour (0–23)
        │ │ │ ┌───────── day of month (1–31)
        │ │ │ │ ┌─────── month (1–12)
        │ │ │ │ │ ┌───── day of week (0–6, Sun=0)
        │ │ │ │ │ │
        * * * * * *
        ```

        Timing is evaluated in the agent's configured timezone.

        ### Common Patterns

        | Expression          | Format | Meaning                             |
        |---------------------|--------|-------------------------------------|
        | `0 8 * * 1-5`      | 5-field | Weekdays at 8:00 AM                |
        | `0 9 * * *`        | 5-field | Every day at 9:00 AM               |
        | `0 17 * * 5`       | 5-field | Fridays at 5:00 PM                 |
        | `0 12 1 * *`       | 5-field | 1st of each month at noon          |
        | `*/15 * * * *`     | 5-field | Every 15 minutes                   |
        | `0 8,12,17 * * 1-5`| 5-field | Weekdays at 8 AM, noon, and 5 PM   |
        | `0 0 9 * * *`      | 6-field | Every day at 9:00:00 AM            |
        | `30 * * * * *`     | 6-field | Every minute at :30 seconds        |
        | `*/10 * * * * *`   | 6-field | Every 10 seconds                   |

        ### Special Values

        - `*` — every value
        - `*/n` — every nth value (e.g. `*/5` every 5 units)
        - `n-m` — range (e.g. `1-5` Mon–Fri)
        - `n,m` — list (e.g. `8,12,17` at hours 8, 12, and 17)


        ## One-Time Tasks

        To fire once at a specific future time, pin all fields to that exact moment.
        Your system prompt contains the current date and time — use it to compute the
        target.

        **Example:** Current time is 14:22:10 on March 5. User says "remind me in 2 minutes."
        Target = 14:24. Use 5-field: `24 14 5 3 *`

        **Example:** Current time is 14:22:45 on March 5. User says "do this in 30 seconds."
        Target = 14:23:15. Use 6-field: `15 23 14 5 3 *`

        **Example:** User says "remind me at 3 PM today" and today is March 5.
        Use 5-field: `0 15 5 3 *`

        One-time tasks remain in the task list after they fire (at their next
        occurrence, which may be a year away for day+month pins). Cancel them after
        they fire if they are no longer needed.


        ## Relative-Time Requests

        When the user says "in X minutes/seconds/hours":
        1. Read the current time from your system prompt.
        2. Add the offset to compute the target datetime.
        3. Choose 5-field for minute-or-greater offsets, 6-field for second-level.
        4. Pin the target fields; use `*` for day-of-week to avoid the AND logic trap.

        ⚠️ **AND logic trap**: When both day-of-month AND day-of-week are specified
        (not `*`), most cron libraries require BOTH to match. Always set day-of-week
        to `*` for one-time tasks unless you specifically want a weekday constraint.


        ## Task Descriptions — Best Practices

        The description is the agent's instruction when the task fires. Write it as a
        clear, self-contained action:

        **Good:**
        - "Say hello to the user."
        - "Check my inbox for unread emails from the past 24 hours and summarise them."
        - "Search for the latest AI news and write a brief digest."

        **Too vague:**
        - "Check email" (what should I do with it?)
        - "Do morning tasks" (which tasks?)


        ## Persistence

        Tasks survive agent restarts — stored on disk and reloaded automatically.
        Missed runs are not back-filled; the task fires at its next scheduled occurrence.


        ## Workflow Examples

        **Recurring — weekday morning briefing:**
        ```
        schedule_task(
          name: "morning-calendar-check",
          cron: "0 8 * * 1-5",
          description: "Check my calendar for today's events and summarise what's coming up."
        )
        ```

        **One-time — reminder in 5 minutes (current time 09:17, day 5, month 3):**
        ```
        schedule_task(
          name: "five-min-reminder",
          cron: "22 9 5 3 *",
          description: "Remind the user that 5 minutes have passed."
        )
        ```

        **One-time — in 45 seconds (current time 09:17:20, day 5, month 3):**
        ```
        schedule_task(
          name: "quick-ping",
          cron: "5 18 9 5 3 *",
          description: "Say hello to the user."
        )
        ```
        """;
}
