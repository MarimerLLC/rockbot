using Microsoft.Extensions.Logging;
using RockBot.Messaging;

namespace RockBot.Host.Middleware;

/// <summary>
/// Catches exceptions and converts them to Retry results.
/// </summary>
public sealed class ErrorHandlingMiddleware : IMiddleware
{
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(ILogger<ErrorHandlingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task InvokeAsync(MessageHandlerContext context, MessageHandlerDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Message {MessageId} processing was cancelled", context.Envelope.MessageId);
            context.Result = MessageResult.Retry;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message {MessageId}", context.Envelope.MessageId);
            context.Result = MessageResult.Retry;
        }
    }
}
