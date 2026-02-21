using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Subagent;

/// <summary>
/// Singleton manager for spawning and tracking subagent tasks.
/// </summary>
public sealed class SubagentManager(
    IServiceScopeFactory scopeFactory,
    IOptions<SubagentOptions> options,
    ILogger<SubagentManager> logger) : ISubagentManager
{
    private readonly ConcurrentDictionary<string, SubagentEntry> _active = new();

    public async Task<string> SpawnAsync(
        string description,
        string? context,
        int? timeoutMinutes,
        string primarySessionId,
        CancellationToken ct)
    {
        // Clean up completed tasks first
        foreach (var key in _active.Keys.ToList())
        {
            if (_active.TryGetValue(key, out var entry) && entry.Task.IsCompleted)
                _active.TryRemove(key, out _);
        }

        var opts = options.Value;
        if (_active.Count >= opts.MaxConcurrentSubagents)
        {
            logger.LogWarning("Subagent limit reached ({Max}); rejecting spawn request", opts.MaxConcurrentSubagents);
            return $"Error: maximum concurrent subagents ({opts.MaxConcurrentSubagents}) already running. " +
                   $"Use list_subagents to see active tasks or cancel_subagent to free a slot.";
        }

        var taskId = Guid.NewGuid().ToString("N")[..12];
        var subagentSessionId = $"subagent-{taskId}";
        var timeout = timeoutMinutes ?? opts.DefaultTimeoutMinutes;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(timeout));

        var task = RunSubagentAsync(taskId, subagentSessionId, description, context, primarySessionId, cts.Token);

        var newEntry = new SubagentEntry
        {
            TaskId = taskId,
            SubagentSessionId = subagentSessionId,
            PrimarySessionId = primarySessionId,
            Description = description,
            StartedAt = DateTimeOffset.UtcNow,
            CancellationTokenSource = cts,
            Task = task
        };

        _active[taskId] = newEntry;

        logger.LogInformation(
            "Spawned subagent {TaskId} (session {SessionId}) for primary session {PrimarySessionId}",
            taskId, subagentSessionId, primarySessionId);

        return taskId;
    }

    public async Task<bool> CancelAsync(string taskId)
    {
        if (!_active.TryGetValue(taskId, out var entry))
            return false;

        await entry.CancellationTokenSource.CancelAsync();
        try { await entry.Task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
        _active.TryRemove(taskId, out _);
        logger.LogInformation("Cancelled subagent {TaskId}", taskId);
        return true;
    }

    public IReadOnlyList<SubagentEntry> ListActive()
    {
        // Clean completed
        foreach (var key in _active.Keys.ToList())
        {
            if (_active.TryGetValue(key, out var e) && e.Task.IsCompleted)
                _active.TryRemove(key, out _);
        }
        return _active.Values.ToList();
    }

    private async Task RunSubagentAsync(
        string taskId,
        string subagentSessionId,
        string description,
        string? context,
        string primarySessionId,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<SubagentRunner>();

        try
        {
            await runner.RunAsync(taskId, subagentSessionId, description, context, primarySessionId, ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Subagent {TaskId} was cancelled", taskId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subagent {TaskId} failed unexpectedly", taskId);
        }
        finally
        {
            _active.TryRemove(taskId, out _);
        }
    }
}
