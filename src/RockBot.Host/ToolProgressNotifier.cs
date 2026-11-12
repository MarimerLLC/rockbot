using Microsoft.Extensions.Logging;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Ambient context that carries session routing info for tool-call progress messages.
/// Each handler sets this via <see cref="ToolProgressNotifier.SetContext"/> before calling
/// <see cref="AgentLoopRunner.RunAsync"/> so the notifier knows where to route progress.
/// </summary>
public sealed class ToolProgressContext
{
    public required string SessionId { get; init; }
    public required string AgentName { get; init; }
    public string? CorrelationId { get; init; }
    public required string ReplyTo { get; init; }

    /// <summary>
    /// Which part of the agent the run belongs to, stamped on every progress reply and event.
    /// Null is read as <see cref="ReplySource.Primary"/>.
    /// </summary>
    public ReplySource? Source { get; init; }

    /// <summary>
    /// The originating proxy advertised <see cref="ClientCapabilities.AgentEvents"/>, so
    /// correlated events (run lifecycle, task lists) may go to <see cref="ReplyTo"/>.
    /// </summary>
    public bool EventsEnabled { get; init; }

    /// <summary>The raw session id events report as their thread. Defaults to <see cref="SessionId"/>.</summary>
    public string? ThreadId { get; init; }

    /// <summary>The user turn this run belongs to, whose heartbeat carries the latest tool call. Null outside a user turn.</summary>
    public TurnLifecycle? Turn { get; init; }

    /// <summary>The effective <see cref="ReplySource"/>.</summary>
    public ReplySource EffectiveSource => Source ?? ReplySource.Primary;

    /// <summary>The effective thread id: <see cref="ThreadId"/>, else <see cref="SessionId"/> without a <c>session/</c> prefix.</summary>
    public string EffectiveThreadId => ThreadId ?? SessionIds.Raw(SessionId);
}

/// <summary>Helpers for the two forms a session id takes.</summary>
public static class SessionIds
{
    private const string Prefix = "session/";

    /// <summary>The raw session id, with the working-memory <c>session/</c> prefix removed.</summary>
    public static string Raw(string sessionId) =>
        sessionId.StartsWith(Prefix, StringComparison.Ordinal) ? sessionId[Prefix.Length..] : sessionId;
}

/// <summary>
/// Publishes per-tool-call progress messages to the message bus so the Blazor UI can
/// display a step-by-step activity log. Uses <see cref="AsyncLocal{T}"/> to read the
/// session routing context set by each handler.
/// </summary>
public sealed class ToolProgressNotifier(
    IMessagePublisher publisher,
    ILogger<ToolProgressNotifier> logger) : IToolProgressNotifier
{
    private static readonly AsyncLocal<ToolProgressContext?> _context = new();

    /// <summary>The ambient context of the current async flow, or null.</summary>
    public static ToolProgressContext? Current => _context.Value;

    /// <summary>
    /// Sets the ambient context for the current async flow. Call this before
    /// <see cref="AgentLoopRunner.RunAsync"/> so tool-call progress is routed correctly.
    /// Returns an <see cref="IDisposable"/> that restores the previous context on dispose, so a
    /// subagent run nested inside a tool call hands the context back to the run that spawned it.
    /// </summary>
    public static IDisposable SetContext(ToolProgressContext context)
    {
        var previous = _context.Value;
        _context.Value = context;
        return new ContextScope(previous);
    }

    public async Task OnToolInvokingAsync(string toolName, string? argsSummary, CancellationToken ct)
    {
        var ctx = _context.Value;
        if (ctx is null) return;

        var content = StartedText(toolName, argsSummary);
        ctx.Turn?.UpdateStatus(content);
        await PublishProgressAsync(ctx, content, toolCall: null, ct);
    }

    public async Task OnToolInvokedAsync(string toolName, string? resultSummary, CancellationToken ct)
    {
        var ctx = _context.Value;
        if (ctx is null) return;

        await PublishProgressAsync(ctx, FinishedText(toolName, resultSummary), toolCall: null, ct);
    }

    public async Task OnToolCallAsync(ToolCallInfo info, CancellationToken ct)
    {
        var ctx = _context.Value;
        if (ctx is null) return;

        var content = info.Phase switch
        {
            ToolCallPhase.Started => StartedText(info.ToolCallName, info.ArgsSummary),
            ToolCallPhase.Failed => $"{info.ToolCallName} failed: {info.Error ?? "unknown error"}",
            _ => FinishedText(info.ToolCallName, info.Result),
        };

        if (info.Phase == ToolCallPhase.Started)
            ctx.Turn?.UpdateStatus(content);
        await PublishProgressAsync(ctx, content, info, ct);
    }

    private static string StartedText(string toolName, string? argsSummary) =>
        string.IsNullOrEmpty(argsSummary) ? $"Calling {toolName}…" : $"Calling {argsSummary}…";

    private static string FinishedText(string toolName, string? resultSummary) =>
        string.IsNullOrEmpty(resultSummary) ? $"{toolName} completed" : $"{toolName} → {resultSummary}";

    private async Task PublishProgressAsync(ToolProgressContext ctx, string content, ToolCallInfo? toolCall, CancellationToken ct)
    {
        var reply = new AgentReply
        {
            Content = content,
            SessionId = ctx.SessionId,
            AgentName = ctx.AgentName,
            IsFinal = false,
            ToolCall = toolCall,
            Source = ctx.EffectiveSource,
        };

        var envelope = reply.ToEnvelope<AgentReply>(
            source: ctx.AgentName,
            correlationId: ctx.CorrelationId);

        try
        {
            await publisher.PublishAsync(ctx.ReplyTo, envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to publish tool progress for {Tool}", content);
        }
    }

    private sealed class ContextScope(ToolProgressContext? previous) : IDisposable
    {
        public void Dispose() => _context.Value = previous;
    }
}

/// <summary>
/// The id of the tool call currently executing in this async flow, so work it spawns (a
/// subagent, a worker) can name the call that started it.
/// </summary>
public static class CurrentToolCallContext
{
    private static readonly AsyncLocal<string?> _callId = new();

    /// <summary>The executing tool call's id, or null outside a tool call.</summary>
    public static string? CallId => _callId.Value;

    /// <summary>Sets the executing call id until the returned scope is disposed.</summary>
    public static IDisposable Set(string? callId)
    {
        var previous = _callId.Value;
        _callId.Value = callId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => _callId.Value = previous;
    }
}
