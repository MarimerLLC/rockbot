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

    /// <summary>
    /// Sets the ambient context for the current async flow. Call this before
    /// <see cref="AgentLoopRunner.RunAsync"/> so tool-call progress is routed correctly.
    /// Returns an <see cref="IDisposable"/> that clears the context on dispose.
    /// </summary>
    public static IDisposable SetContext(ToolProgressContext context)
    {
        _context.Value = context;
        return new ContextScope();
    }

    public async Task OnToolInvokingAsync(string toolName, string? argsSummary, CancellationToken ct)
    {
        var ctx = _context.Value;
        if (ctx is null) return;

        var content = string.IsNullOrEmpty(argsSummary)
            ? $"Calling {toolName}…"
            : $"Calling {argsSummary}…";

        await PublishProgressAsync(ctx, content, ct);
    }

    public async Task OnToolInvokedAsync(string toolName, string? resultSummary, CancellationToken ct)
    {
        var ctx = _context.Value;
        if (ctx is null) return;

        var content = string.IsNullOrEmpty(resultSummary)
            ? $"{toolName} completed"
            : $"{toolName} \u2192 {resultSummary}";

        await PublishProgressAsync(ctx, content, ct);
    }

    private async Task PublishProgressAsync(ToolProgressContext ctx, string content, CancellationToken ct)
    {
        var reply = new AgentReply
        {
            Content = content,
            SessionId = ctx.SessionId,
            AgentName = ctx.AgentName,
            IsFinal = false,
            AgentVersion = AgentReply.CurrentVersion
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

    private sealed class ContextScope : IDisposable
    {
        public void Dispose() => _context.Value = null;
    }
}
