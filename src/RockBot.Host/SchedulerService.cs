using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;

namespace RockBot.Host;

/// <summary>
/// Hosted service that loads persisted scheduled tasks at startup, arms per-task timers,
/// and dispatches <see cref="ScheduledTaskMessage"/> through the agent pipeline when
/// a cron fires. Also implements <see cref="ISchedulerService"/> so tool executors can
/// schedule and cancel tasks at runtime.
/// </summary>
internal sealed class SchedulerService : IHostedService, ISchedulerService
{
    private readonly IScheduledTaskStore _store;
    private readonly IMessagePipeline _pipeline;
    private readonly AgentClock _clock;
    private readonly AgentIdentity _identity;
    private readonly ILogger<SchedulerService> _logger;

    private readonly Dictionary<string, Timer> _timers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _timerLock = new();
    private CancellationTokenSource _cts = new();

    public SchedulerService(
        IScheduledTaskStore store,
        IMessagePipeline pipeline,
        AgentClock clock,
        AgentIdentity identity,
        ILogger<SchedulerService> logger)
    {
        _store = store;
        _pipeline = pipeline;
        _clock = clock;
        _identity = identity;
        _logger = logger;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var tasks = await _store.ListAsync();
        foreach (var task in tasks)
        {
            ArmTimer(task);
            _logger.LogInformation("Loaded scheduled task '{Name}' ({Cron})", task.Name, task.CronExpression);
        }

        _logger.LogInformation("Scheduler started with {Count} task(s)", tasks.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();

        lock (_timerLock)
        {
            foreach (var timer in _timers.Values)
                timer.Dispose();
            _timers.Clear();
        }

        _logger.LogInformation("Scheduler stopped");
        return Task.CompletedTask;
    }

    // ── ISchedulerService ─────────────────────────────────────────────────────

    public async Task ScheduleAsync(ScheduledTask task, CancellationToken ct = default)
    {
        await _store.SaveAsync(task);
        ArmTimer(task);
        _logger.LogInformation(
            "Scheduled task '{Name}' with cron '{Cron}'", task.Name, task.CronExpression);
    }

    public async Task<bool> CancelAsync(string name, CancellationToken ct = default)
    {
        var deleted = await _store.DeleteAsync(name);
        if (!deleted)
            return false;

        lock (_timerLock)
        {
            if (_timers.TryGetValue(name, out var timer))
            {
                timer.Dispose();
                _timers.Remove(name);
            }
        }

        _logger.LogInformation("Cancelled scheduled task '{Name}'", name);
        return true;
    }

    public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct = default)
        => _store.ListAsync();

    // ── Internals ─────────────────────────────────────────────────────────────

    // System.Threading.Timer requires dueTime < uint.MaxValue milliseconds (~49.7 days).
    // We cap at 24 hours and re-arm in the callback when the target time hasn't been reached.
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromHours(24);

    private void ArmTimer(ScheduledTask task)
    {
        CronExpression cron;
        try
        {
            cron = ParseCron(task.CronExpression);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Invalid cron expression for task '{Name}': {Cron}", task.Name, task.CronExpression);
            return;
        }

        var now = _clock.Now;
        var next = cron.GetNextOccurrence(now, _clock.Zone);
        if (next is null)
        {
            _logger.LogWarning(
                "No next occurrence for task '{Name}' cron '{Cron}' — task will not fire",
                task.Name, task.CronExpression);
            return;
        }

        ArmTimerForTarget(task, next.Value);
    }

    private void ArmTimerForTarget(ScheduledTask task, DateTimeOffset target)
    {
        var now = _clock.Now;
        var delay = target - now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        // Cap delay to avoid Timer overflow; callback re-checks if not yet at target time.
        var timerDelay = delay > MaxTimerDelay ? MaxTimerDelay : delay;

        lock (_timerLock)
        {
            if (_timers.TryGetValue(task.Name, out var old))
                old.Dispose();

            _timers[task.Name] = new Timer(
                _ => _ = OnTimerTickAsync(task, target),
                null,
                timerDelay,
                Timeout.InfiniteTimeSpan);
        }

        _logger.LogDebug(
            "Armed timer for '{Name}': target {Target} (sleeping {Delay:g})",
            task.Name, target, timerDelay);
    }

    private async Task OnTimerTickAsync(ScheduledTask task, DateTimeOffset target)
    {
        if (_cts.IsCancellationRequested) return;

        // If the target time hasn't been reached yet (capped delay), re-arm and wait more.
        if (_clock.Now < target)
        {
            ArmTimerForTarget(task, target);
            return;
        }

        await FireTaskAsync(task);
    }

    private async Task FireTaskAsync(ScheduledTask task)
    {
        if (_cts.IsCancellationRequested) return;

        var firedAt = _clock.Now;
        _logger.LogInformation("Firing scheduled task '{Name}'", task.Name);

        try
        {
            var message = new ScheduledTaskMessage(task.Name, task.Description);
            var envelope = message.ToEnvelope(source: _identity.Name);
            await _pipeline.DispatchAsync(envelope, _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing scheduled task '{Name}'", task.Name);
        }

        await _store.UpdateLastFiredAsync(task.Name, firedAt);

        if (task.RunOnce)
        {
            // One-time task — delete from store and remove the timer slot.
            await _store.DeleteAsync(task.Name);
            lock (_timerLock) { _timers.Remove(task.Name); }
            _logger.LogInformation("One-time task '{Name}' completed and removed", task.Name);
            return;
        }

        // Re-arm for the next occurrence
        var updated = task with { LastFiredAt = firedAt };
        ArmTimer(updated);
    }

    private static CronExpression ParseCron(string expression)
    {
        // Try standard 5-field first; fall back to 6-field (with seconds)
        try
        {
            return CronExpression.Parse(expression, CronFormat.Standard);
        }
        catch (CronFormatException)
        {
            return CronExpression.Parse(expression, CronFormat.IncludeSeconds);
        }
    }
}
