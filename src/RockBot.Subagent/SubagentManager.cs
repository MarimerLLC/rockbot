using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Subagent;

/// <summary>
/// Singleton manager for spawning and tracking subagent tasks.
/// </summary>
public sealed class SubagentManager(
    IServiceScopeFactory scopeFactory,
    IOptions<SubagentOptions> options,
    IMessagePublisher publisher,
    AgentIdentity agentIdentity,
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
        // SubagentRunner.RunAsync handles all its own exit paths (success, failure,
        // cancellation) and always publishes a SubagentResultMessage before returning.
        // This outer try/catch covers failures that occur before the runner even starts
        // (e.g. DI resolution errors) so the primary agent is always notified.
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<SubagentRunner>();
            await runner.RunAsync(taskId, subagentSessionId, description, context, primarySessionId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subagent {TaskId} failed before runner could start", taskId);

            // Publish a failure result so the primary agent is always informed,
            // even when the runner never started.
            try
            {
                var result = new SubagentResultMessage
                {
                    TaskId = taskId,
                    SubagentSessionId = subagentSessionId,
                    PrimarySessionId = primarySessionId,
                    Output = $"Subagent failed to start: {ex.Message}",
                    IsSuccess = false,
                    Error = ex.Message,
                    Timestamp = DateTimeOffset.UtcNow
                };
                var envelope = result.ToEnvelope<SubagentResultMessage>(source: agentIdentity.Name);
                await publisher.PublishAsync(SubagentTopics.Result, envelope, CancellationToken.None);
            }
            catch (Exception pubEx)
            {
                logger.LogError(pubEx, "Failed to publish failure result for subagent {TaskId}", taskId);
            }
        }
        finally
        {
            _active.TryRemove(taskId, out _);
        }
    }
}
