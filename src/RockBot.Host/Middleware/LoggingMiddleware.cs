using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RockBot.Host.Middleware;

/// <summary>
/// Logs message dispatch entry/exit with timing information.
/// </summary>
public sealed class LoggingMiddleware : IMiddleware
{
    private readonly ILogger<LoggingMiddleware> _logger;

    public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task InvokeAsync(MessageHandlerContext context, MessageHandlerDelegate next)
    {
        var envelope = context.Envelope;
        _logger.LogInformation(
            "Dispatching message {MessageId} type={MessageType} correlation={CorrelationId}",
            envelope.MessageId, envelope.MessageType, envelope.CorrelationId);

        var sw = Stopwatch.StartNew();
        await next(context);
        sw.Stop();

        _logger.LogInformation(
            "Completed message {MessageId} result={Result} elapsed={ElapsedMs}ms",
            envelope.MessageId, context.Result, sw.ElapsedMilliseconds);
    }
}
