using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Registers the heartbeat-patrol scheduled task on startup if it does not
/// already exist. Idempotent — safe to run on every pod restart.
/// </summary>
internal sealed class HeartbeatBootstrapService(
    ISchedulerService scheduler,
    IOptions<HeartbeatBootstrapOptions> options,
    ILogger<HeartbeatBootstrapService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Heartbeat patrol bootstrap is disabled; skipping registration");
            return;
        }

        var existing = await scheduler.ListAsync(ct);
        var patrol = existing.FirstOrDefault(t => t.Name == "heartbeat-patrol");

        if (patrol is not null && patrol.CronExpression == options.Value.CronExpression)
        {
            logger.LogInformation("Heartbeat patrol already registered with cron '{Cron}'; skipping", patrol.CronExpression);
            return;
        }

        await scheduler.ScheduleAsync(new ScheduledTask(
            Name: "heartbeat-patrol",
            CronExpression: options.Value.CronExpression,
            Description: "Run the heartbeat patrol: check calendar, email, active plans, and scheduled task health.",
            CreatedAt: patrol?.CreatedAt ?? DateTimeOffset.UtcNow,
            RunOnce: false), ct);

        var action = patrol is null ? "Registered" : "Updated";
        logger.LogInformation("{Action} heartbeat patrol (cron: {Cron})", action, options.Value.CronExpression);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
