using System.Collections.Concurrent;
using System.Diagnostics;
using RockBot.Host;
using RockBot.Llm.Copilot;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Bridges <see cref="ICopilotSessionEvents"/> to <see cref="IToolProgressNotifier"/>
/// so that tool-call progress from Copilot sessions is surfaced to the user's UI.
/// Uses deferred resolution: the <see cref="IServiceProvider"/> is set after the host
/// is built, since Copilot clients are constructed during startup before DI is ready.
/// </summary>
internal sealed class CopilotSessionEventsBridge : ICopilotSessionEvents
{
    private IServiceProvider? _serviceProvider;

    // Copilot reports no call ids, so a start is paired with the next completion of the same
    // tool (#795). Starts are queued per tool name, oldest first.
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(string Id, long StartedAt)>> _inFlight = new();

    /// <summary>
    /// Sets the service provider after the host is built. Must be called before
    /// any tool events fire (safe because sessions aren't created until messages arrive).
    /// </summary>
    public void SetServiceProvider(IServiceProvider sp) => _serviceProvider = sp;

    private IToolProgressNotifier? Notifier =>
        _serviceProvider?.GetService(typeof(IToolProgressNotifier)) as IToolProgressNotifier;

    public Task OnToolStartAsync(string toolName, string? arguments, CancellationToken ct)
    {
        var notifier = Notifier;
        if (notifier is null)
            return Task.CompletedTask;

        var id = Guid.NewGuid().ToString("N");
        _inFlight.GetOrAdd(toolName, _ => new()).Enqueue((id, Stopwatch.GetTimestamp()));
        return notifier.OnToolCallAsync(new ToolCallInfo
        {
            ToolCallId = id,
            ToolCallName = toolName,
            Phase = ToolCallPhase.Started,
            ArgsSummary = arguments,
        }, ct);
    }

    public Task OnToolCompleteAsync(string toolName, bool success, string? resultSummary, CancellationToken ct)
    {
        var notifier = Notifier;
        if (notifier is null)
            return Task.CompletedTask;

        string id;
        long? durationMs = null;
        if (_inFlight.TryGetValue(toolName, out var queue) && queue.TryDequeue(out var start))
        {
            id = start.Id;
            durationMs = (long)Stopwatch.GetElapsedTime(start.StartedAt).TotalMilliseconds;
        }
        else
        {
            id = Guid.NewGuid().ToString("N");
        }

        return notifier.OnToolCallAsync(new ToolCallInfo
        {
            ToolCallId = id,
            ToolCallName = toolName,
            Phase = success ? ToolCallPhase.Finished : ToolCallPhase.Failed,
            Result = resultSummary,
            DurationMs = durationMs,
            Error = success ? null : resultSummary ?? "failed",
        }, ct);
    }
}
