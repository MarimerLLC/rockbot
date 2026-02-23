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
        if (existing.Any(t => t.Name == "heartbeat-patrol"))
        {
            logger.LogInformation("Heartbeat patrol task already registered; skipping");
            return;
        }

        await scheduler.ScheduleAsync(new ScheduledTask(
            Name: "heartbeat-patrol",
            CronExpression: options.Value.CronExpression,
            Description: "Run the heartbeat patrol: check calendar, email, active plans, and scheduled task health.",
            CreatedAt: DateTimeOffset.UtcNow,
            RunOnce: false), ct);

        logger.LogInformation("Registered heartbeat patrol (cron: {Cron})", options.Value.CronExpression);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
