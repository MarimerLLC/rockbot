using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Registers the heartbeat-patrol scheduled task on startup if it does not
/// already exist. Idempotent — safe to run on every pod restart.
///
/// Also performs a one-time migration: if a legacy <c>patrol/proactive-actions</c> skill
/// exists in the skill store (the previous home for the patrol's evolving checklist), its
/// content is copied into the heartbeat-patrol task's <see cref="ScheduledTask.Directive"/>
/// and the skill is removed. After migration the heartbeat patrol updates itself via the
/// <c>update_task_directive</c> tool instead of the skill store.
/// </summary>
internal sealed class HeartbeatBootstrapService(
    ISchedulerService scheduler,
    IScheduledTaskStore taskStore,
    ISkillStore skillStore,
    IOptions<HeartbeatBootstrapOptions> options,
    ILogger<HeartbeatBootstrapService> logger) : IHostedService
{
    private const string TaskName = "heartbeat-patrol";
    private const string LegacySkillName = "patrol/proactive-actions";

    public async Task StartAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Heartbeat patrol bootstrap is disabled; skipping registration");
            return;
        }

        var existing = await scheduler.ListAsync(ct);
        var patrol = existing.FirstOrDefault(t => t.Name == TaskName);

        // Migration: if the legacy skill exists, fold its content into the task's Directive.
        // Prefer any directive that's already on the task — the agent has likely refined it
        // beyond the skill's contents.
        var legacySkill = await skillStore.GetAsync(LegacySkillName);
        var directiveSeed = patrol?.Directive ?? legacySkill?.Content;

        var cronUnchanged = patrol is not null && patrol.CronExpression == options.Value.CronExpression;
        var directiveSeedDiffers = !string.Equals(patrol?.Directive, directiveSeed, StringComparison.Ordinal);

        if (cronUnchanged && !directiveSeedDiffers)
        {
            logger.LogInformation(
                "Heartbeat patrol already registered with cron '{Cron}' and directive in place; skipping",
                patrol!.CronExpression);
        }
        else if (cronUnchanged)
        {
            // Same cron, but the directive is missing — only the legacy skill's content needs
            // to be folded in. UpdateDirectiveAsync preserves CreatedAt/LastFiredAt.
            await taskStore.UpdateDirectiveAsync(TaskName, directiveSeed!);
            logger.LogInformation(
                "Seeded heartbeat patrol directive from legacy skill '{Skill}' ({Length} chars)",
                LegacySkillName, directiveSeed!.Length);
        }
        else
        {
            await scheduler.ScheduleAsync(new ScheduledTask(
                Name: TaskName,
                CronExpression: options.Value.CronExpression,
                Description: "Run the heartbeat patrol: check calendar, email, active plans, and scheduled task health.",
                CreatedAt: patrol?.CreatedAt ?? DateTimeOffset.UtcNow,
                RunOnce: false,
                IsSystemTask: true,
                Directive: directiveSeed), ct);

            var action = patrol is null ? "Registered" : "Updated";
            logger.LogInformation("{Action} heartbeat patrol (cron: {Cron})", action, options.Value.CronExpression);
        }

        // Migration: drop the legacy skill once its content has reached the task.
        if (legacySkill is not null)
        {
            await skillStore.DeleteAsync(LegacySkillName);
            logger.LogInformation(
                "Migrated legacy skill '{Skill}' into '{Task}' directive and removed the skill",
                LegacySkillName, TaskName);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
