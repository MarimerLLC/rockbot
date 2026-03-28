using Microsoft.Extensions.Logging;

namespace RockBot.Host.Middleware;

/// <summary>
/// Persists the incoming message envelope to the WIP store before dispatching
/// to the handler, and auto-completes the entry when the handler returns
/// — unless the handler set <see cref="WipConstants.DeferredKey"/> in
/// <see cref="MessageHandlerContext.Items"/> to indicate a background loop
/// will complete the WIP explicitly.
/// </summary>
public sealed class WipMiddleware(IWipTracker wipTracker, ILogger<WipMiddleware> logger) : IMiddleware
{
    public async Task InvokeAsync(MessageHandlerContext context, MessageHandlerDelegate next)
    {
        var entry = await wipTracker.BeginAsync(context.Envelope, context.CancellationToken);
        context.Items[WipConstants.MessageIdKey] = entry.MessageId;

        try
        {
            await next(context);
        }
        catch
        {
            // Let the exception propagate — ErrorHandlingMiddleware will catch it.
            // The WIP entry remains on disk for recovery on next startup.
            logger.LogDebug("WIP entry {MessageId} will remain for recovery after exception",
                entry.MessageId);
            throw;
        }

        // Auto-complete only if the handler did NOT defer completion to a background loop.
        if (!context.Items.ContainsKey(WipConstants.DeferredKey))
        {
            // Use CancellationToken.None — we must clean up the WIP entry even during
            // graceful shutdown to avoid spurious recovery on next startup.
            await wipTracker.CompleteAsync(entry.MessageId, CancellationToken.None);
        }
        else
        {
            logger.LogDebug("WIP entry {MessageId} deferred — background loop will complete",
                entry.MessageId);
        }
    }
}
