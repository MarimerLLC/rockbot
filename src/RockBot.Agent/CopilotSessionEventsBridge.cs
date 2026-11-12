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
    // A start that never sees its completion is dropped after this long.
    internal static readonly TimeSpan InFlightRetention = TimeSpan.FromHours(1);

    private IServiceProvider? _serviceProvider;

    // #795: the SDK's completion carries only the call id, so the start's tool name and time are
    // kept under that id until it arrives.
    private readonly ConcurrentDictionary<string, (string ToolName, long StartedAt)> _inFlight = new();

    /// <summary>
    /// Sets the service provider after the host is built. Must be called before
    /// any tool events fire (safe because sessions aren't created until messages arrive).
    /// </summary>
    public void SetServiceProvider(IServiceProvider sp) => _serviceProvider = sp;

    private IToolProgressNotifier? Notifier =>
        _serviceProvider?.GetService(typeof(IToolProgressNotifier)) as IToolProgressNotifier;

    /// <summary>Calls started and not yet completed. For tests.</summary>
    internal int InFlightCount => _inFlight.Count;

    public Task OnToolStartAsync(string toolCallId, string toolName, string? arguments, CancellationToken ct)
    {
        var notifier = Notifier;
        if (notifier is null)
            return Task.CompletedTask;

        PruneStale();
        _inFlight[toolCallId] = (toolName, Stopwatch.GetTimestamp());
        return notifier.OnToolCallAsync(new ToolCallInfo
        {
            ToolCallId = toolCallId,
            ToolCallName = toolName,
            Phase = ToolCallPhase.Started,
            ArgsSummary = arguments,
        }, ct);
    }

    public Task OnToolCompleteAsync(string toolCallId, bool success, string? resultSummary, CancellationToken ct)
    {
        var notifier = Notifier;
        if (notifier is null)
            return Task.CompletedTask;

        var toolName = "tool";
        long? durationMs = null;
        if (_inFlight.TryRemove(toolCallId, out var start))
        {
            toolName = start.ToolName;
            durationMs = (long)Stopwatch.GetElapsedTime(start.StartedAt).TotalMilliseconds;
        }

        return notifier.OnToolCallAsync(new ToolCallInfo
        {
            ToolCallId = toolCallId,
            ToolCallName = toolName,
            Phase = success ? ToolCallPhase.Finished : ToolCallPhase.Failed,
            Result = resultSummary,
            DurationMs = durationMs,
            Error = success ? null : resultSummary ?? "failed",
        }, ct);
    }

    private void PruneStale()
    {
        foreach (var (id, start) in _inFlight)
        {
            if (Stopwatch.GetElapsedTime(start.StartedAt) > InFlightRetention)
                _inFlight.TryRemove(id, out _);
        }
    }
}
