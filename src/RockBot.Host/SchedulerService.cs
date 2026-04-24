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
    private readonly IAgentWorkSerializer _workSerializer;
    private readonly ILogger<SchedulerService> _logger;

    // Retry policy when a cron fires but a user session holds the work slot:
    // re-arm the task every RetryDelay for up to MaxRetryAttempts, then fall
    // back to the next natural cron occurrence.
    private const int MaxRetryAttempts = 15;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, Timer> _timers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _retryAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _timerLock = new();
    private CancellationTokenSource _cts = new();

    public SchedulerService(
        IScheduledTaskStore store,
        IMessagePipeline pipeline,
        AgentClock clock,
        AgentIdentity identity,
        IAgentWorkSerializer workSerializer,
        ILogger<SchedulerService> logger)
    {
        _store = store;
        _pipeline = pipeline;
        _clock = clock;
        _identity = identity;
        _workSerializer = workSerializer;
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

    public DateTimeOffset? GetNextOccurrence(ScheduledTask task)
    {
        try
        {
            var cron = ParseCron(task.CronExpression);
            return cron.GetNextOccurrence(_clock.Now, _clock.Zone);
        }
        catch
        {
            return null;
        }
    }

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
                "No next occurrence for task '{Name}' cron '{Cron}' — removing orphaned task",
                task.Name, task.CronExpression);
            _ = _store.DeleteAsync(task.Name);
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

        // Acquire the work slot before dispatching. If a user session already
        // holds it, we'd only have dispatched a message that the handler would
        // immediately skip — so retry in a few minutes instead.
        var slot = await _workSerializer.TryAcquireForScheduledAsync(_cts.Token);
        if (slot is null)
        {
            _logger.LogInformation(
                "Scheduled task '{Name}' preempted by active user session — scheduling retry",
                task.Name);
            ScheduleRetry(task);
            return;
        }

        var firedAt = _clock.Now;
        var preempted = false;
        var handlerCompleted = false;

        await using (slot)
        {
            _logger.LogInformation("Firing scheduled task '{Name}'", task.Name);
            try
            {
                var message = new ScheduledTaskMessage(task.Name, task.Description, task.IsSystemTask);
                var envelope = message.ToEnvelope(source: _identity.Name);
                // Pass the slot's cancellation token so the handler (and the LLM
                // loop it drives) stops cleanly when a user message arrives.
                await _pipeline.DispatchAsync(envelope, slot.Token);
                handlerCompleted = true;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // Host is shutting down — do not retry, do not update state.
                return;
            }
            catch (OperationCanceledException)
            {
                // User session preempted the task mid-run.
                preempted = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing scheduled task '{Name}'", task.Name);
                // Treat errors as "ran" so we don't loop on a broken task — the next
                // cron occurrence will try again.
                handlerCompleted = true;
            }
        }

        if (preempted)
        {
            _logger.LogInformation(
                "Scheduled task '{Name}' preempted mid-run — scheduling retry", task.Name);
            ScheduleRetry(task);
            return;
        }

        if (!handlerCompleted) return;

        await _store.UpdateLastFiredAsync(task.Name, firedAt);
        lock (_timerLock) { _retryAttempts.Remove(task.Name); }

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

    private void ScheduleRetry(ScheduledTask task)
    {
        int attempt;
        lock (_timerLock)
        {
            _retryAttempts.TryGetValue(task.Name, out attempt);
            attempt++;
            if (attempt > MaxRetryAttempts)
            {
                _retryAttempts.Remove(task.Name);
                _logger.LogWarning(
                    "Scheduled task '{Name}' exceeded retry budget ({Max}); falling back to next cron occurrence",
                    task.Name, MaxRetryAttempts);
                ArmTimer(task);
                return;
            }
            _retryAttempts[task.Name] = attempt;
        }

        var target = _clock.Now + RetryDelay;
        _logger.LogInformation(
            "Retry {Attempt}/{Max} for '{Name}' armed for {Target:yyyy-MM-dd HH:mm:ss} ({Zone})",
            attempt, MaxRetryAttempts, task.Name, target, _clock.Zone.Id);
        ArmTimerForTarget(task, target);
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
