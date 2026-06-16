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
    AgentIdentity agent,
    ILogger<SubagentManager> logger) : ISubagentManager, ISubagentSessionResolver
{
    private readonly ConcurrentDictionary<string, SubagentEntry> _active = new();

    // Short-lived record of recently-removed subagents (taskId -> owning primary session).
    // A late A2A reply can arrive after a subagent has exited and been pulled from _active;
    // the tombstone lets ISubagentSessionResolver still recover the primary to fold into.
    // Retention comfortably exceeds the A2A invocation timeout so late replies resolve.
    private static readonly TimeSpan TombstoneRetention = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<string, (string PrimarySessionId, DateTimeOffset RemovedAt)> _tombstones = new();

    public async Task<string> SpawnAsync(
        string description,
        string? context,
        int? timeoutMinutes,
        string primarySessionId,
        CancellationToken ct,
        string? batchId = null,
        bool consolidate = true,
        int? maxIterations = null)
    {
        // Clean up completed tasks first
        foreach (var key in _active.Keys.ToList())
        {
            if (_active.TryGetValue(key, out var entry) && entry.Task.IsCompleted)
                RemoveActive(key);
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
        var timeoutMin = timeoutMinutes ?? opts.DefaultTimeoutMinutes;
        var timeoutSpan = TimeSpan.FromMinutes(timeoutMin);
        // Do NOT link to the caller's ct — that token is the session token which gets
        // canceled when the next user message arrives. Subagents are independent background
        // work that must survive new user messages. They cancel only on their own timeout
        // or via explicit CancelAsync.
        var cts = new CancellationTokenSource();
        cts.CancelAfter(timeoutSpan);

        var task = RunSubagentAsync(taskId, subagentSessionId, description, context, primarySessionId, batchId, consolidate, maxIterations, timeoutSpan, cts.Token);

        var newEntry = new SubagentEntry
        {
            TaskId = taskId,
            SubagentSessionId = subagentSessionId,
            PrimarySessionId = primarySessionId,
            Description = description,
            StartedAt = DateTimeOffset.UtcNow,
            CancellationTokenSource = cts,
            Task = task,
            BatchId = batchId,
            Consolidate = consolidate
        };

        _active[taskId] = newEntry;

        SubagentDiagnostics.Spawns.Add(1);
        SubagentDiagnostics.Active.Add(1);

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
        RemoveActive(taskId);
        logger.LogInformation("Cancelled subagent {TaskId}", taskId);
        return true;
    }

    public IReadOnlyList<SubagentEntry> ListActive()
    {
        // Clean completed
        foreach (var key in _active.Keys.ToList())
        {
            if (_active.TryGetValue(key, out var e) && e.Task.IsCompleted)
                RemoveActive(key);
        }
        return _active.Values.ToList();
    }

    private async Task RunSubagentAsync(
        string taskId,
        string subagentSessionId,
        string description,
        string? context,
        string primarySessionId,
        string? batchId,
        bool consolidate,
        int? maxIterations,
        TimeSpan timeout,
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
            await runner.RunAsync(taskId, subagentSessionId, description, context, primarySessionId, batchId, consolidate, maxIterations, timeout, ct);
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
                    Timestamp = DateTimeOffset.UtcNow,
                    BatchId = batchId,
                    Consolidate = consolidate
                };
                var envelope = result.ToEnvelope<SubagentResultMessage>(source: $"subagent-{taskId}");
                await publisher.PublishAsync($"{SubagentTopics.Result}.{agent.Name}", envelope, CancellationToken.None);
            }
            catch (Exception pubEx)
            {
                logger.LogError(pubEx, "Failed to publish failure result for subagent {TaskId}", taskId);
            }
        }
        finally
        {
            SubagentDiagnostics.Active.Add(-1);
            RemoveActive(taskId);
        }
    }

    /// <summary>
    /// Removes a subagent from the active set and records a tombstone of its owning primary
    /// session so a late A2A reply can still be folded back after the subagent has exited.
    /// </summary>
    private void RemoveActive(string taskId)
    {
        if (_active.TryRemove(taskId, out var entry))
            _tombstones[taskId] = (entry.PrimarySessionId, DateTimeOffset.UtcNow);
        PruneTombstones();
    }

    private void PruneTombstones()
    {
        var cutoff = DateTimeOffset.UtcNow - TombstoneRetention;
        foreach (var (key, value) in _tombstones)
        {
            if (value.RemovedAt < cutoff)
                _tombstones.TryRemove(key, out _);
        }
    }

    // ── ISubagentSessionResolver ──────────────────────────────────────────────
    // A subagent's A2A session id is its working-memory namespace, "subagent/{taskId}"
    // (see SubagentRunner). Defensively also accept "session/subagent-{taskId}" and the
    // bare "subagent-{taskId}" form used elsewhere. _active is keyed by the raw taskId.

    public bool IsSubagentSession(string sessionId) => TryExtractTaskId(sessionId, out _);

    public bool IsActive(string sessionId) =>
        TryExtractTaskId(sessionId, out var taskId)
        && _active.TryGetValue(taskId, out var e)
        && !e.Task.IsCompleted;

    public string? ResolvePrimarySession(string sessionId)
    {
        if (!TryExtractTaskId(sessionId, out var taskId))
            return null;
        if (_active.TryGetValue(taskId, out var entry))
            return entry.PrimarySessionId;
        if (_tombstones.TryGetValue(taskId, out var tomb))
            return tomb.PrimarySessionId;
        return null;
    }

    private static bool TryExtractTaskId(string sessionId, out string taskId)
    {
        taskId = string.Empty;
        if (string.IsNullOrEmpty(sessionId))
            return false;

        foreach (var prefix in new[] { "subagent/", "session/subagent-", "subagent-" })
        {
            if (sessionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                taskId = sessionId[prefix.Length..];
                return taskId.Length > 0;
            }
        }
        return false;
    }
}
