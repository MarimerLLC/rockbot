using RockBot.Host;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.McpBridge.Tasks;

/// <summary>
/// The background MCP tasks this process is running, by session: the caps on how many may run,
/// and a way for a subagent to wait for its own before it reports. A singleton shared by the
/// bridge, which adds and removes tasks, and <see cref="McpTaskSessionAwaiter"/>.
/// </summary>
public sealed class McpTaskRegistry
{
    private readonly Dictionary<string, RunningMcpTask> _tasks = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = NewSignal();

    /// <summary>Tasks running (or starting) now.</summary>
    public int Count
    {
        get { lock (_gate) return _tasks.Count; }
    }

    /// <summary>Tasks running (or starting) now for <paramref name="sessionId"/>.</summary>
    public int CountForSession(string sessionId)
    {
        lock (_gate)
            return _tasks.Values.Count(t => string.Equals(t.SessionId, sessionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Waits until <paramref name="sessionId"/> has no task running, or <paramref name="ct"/> fires.
    /// Returns how many were running when the wait began.
    /// </summary>
    public async Task<int> WaitForSessionAsync(string sessionId, CancellationToken ct)
    {
        var initial = CountForSession(sessionId);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (!_tasks.Values.Any(t => string.Equals(t.SessionId, sessionId, StringComparison.Ordinal)))
                    return initial;
                changed = _changed.Task;
            }

            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Holds a place for a task <paramref name="sessionId"/> is about to start, within the caps.
    /// Returns null with the reason when a cap is reached. A resumed task (after a restart) is
    /// never refused: it is already running on its server.
    /// </summary>
    internal RunningMcpTask? TryReserve(
        string taskId, string sessionId, int maxPerSession, int maxTotal, bool force, out string? refusal)
    {
        lock (_gate)
        {
            refusal = null;
            if (!force)
            {
                var forSession = _tasks.Values.Where(t => string.Equals(t.SessionId, sessionId, StringComparison.Ordinal)).ToList();
                if (forSession.Count >= maxPerSession)
                {
                    refusal = "session";
                    return null;
                }

                if (_tasks.Count >= maxTotal)
                {
                    refusal = "bridge";
                    return null;
                }
            }

            var task = new RunningMcpTask(taskId, sessionId);
            _tasks[taskId] = task;
            Signal();
            return task;
        }
    }

    internal RunningMcpTask? Get(string taskId)
    {
        lock (_gate)
            return _tasks.GetValueOrDefault(taskId);
    }

    internal IReadOnlyList<RunningMcpTask> ForSession(string sessionId)
    {
        lock (_gate)
            return [.. _tasks.Values.Where(t => string.Equals(t.SessionId, sessionId, StringComparison.Ordinal))];
    }

    internal IReadOnlyList<RunningMcpTask> All()
    {
        lock (_gate)
            return [.. _tasks.Values];
    }

    /// <summary>Forgets a task: it ended and its ending was delivered, or it never started.</summary>
    internal void Remove(string taskId)
    {
        lock (_gate)
        {
            if (_tasks.Remove(taskId))
                Signal();
        }
    }

    private void Signal()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// One background task while it runs: what the bridge needs to poll it, answer its questions,
/// map its result, and stop it. Everything durable about it is in its <see cref="McpTaskEntry"/>.
/// </summary>
internal sealed class RunningMcpTask(string taskId, string sessionId)
{
    private string? _cancelReason;

    public string TaskId { get; } = taskId;

    public string SessionId { get; } = sessionId;

    /// <summary>Set once the server created the task; null while it is only reserved.</summary>
    public McpTaskEntry? Entry { get; set; }

    /// <summary>The task's cancellation: the bridge stopping, its deadline, or <see cref="Cancel"/>.</summary>
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>
    /// The original call's arguments as sent to the server, for the response-side attachment
    /// rewrite. Null after a restart (the ledger keeps only redacted arguments); the rewrite is
    /// then skipped and binary capture still applies.
    /// </summary>
    public Dictionary<string, object?>? Arguments { get; set; }

    /// <summary>What the model sent, for error hints on an error result.</summary>
    public Dictionary<string, object?>? SentArguments { get; set; }

    /// <summary>The tool's input schema, for error hints on an error result.</summary>
    public string? InputSchema { get; set; }

    /// <summary>Whether the result goes through the server's attachment rewrite.</summary>
    public bool RewriteResponse { get; set; }

    /// <summary>The task's questions and how they were answered, across rounds and reconnects.</summary>
    public McpElicitationCallScope? Scope { get; set; }

    /// <summary>The server was already asked to cancel it (when its server was removed or reconfigured).</summary>
    public bool CancelledOnServer { get; set; }

    /// <summary>Why the task was cancelled by the bridge or the agent, or null.</summary>
    public string? CancelReason => Volatile.Read(ref _cancelReason);

    /// <summary>The agent asked for the cancellation itself.</summary>
    public bool CancelledByAgent { get; private set; }

    /// <summary>Stops polling and has the loop cancel the task on its server. The first reason wins.</summary>
    public bool Cancel(string reason, bool byAgent = false)
    {
        if (Interlocked.CompareExchange(ref _cancelReason, reason, null) is not null)
            return false;

        CancelledByAgent = byAgent;
        try
        {
            Cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already over.
        }

        return true;
    }
}

/// <summary>
/// Lets a subagent wait for the background MCP tasks it started before it reports, as it waits
/// for its A2A tasks: the tasks' results land among its working-memory outputs, where the
/// primary's synthesis turn finds them, instead of arriving after it is gone.
/// </summary>
public sealed class McpTaskSessionAwaiter(McpTaskRegistry registry) : ISessionA2AAwaiter
{
    public Task<int> WaitForSessionAsync(string sessionId, CancellationToken ct) =>
        registry.WaitForSessionAsync(sessionId, ct);
}
