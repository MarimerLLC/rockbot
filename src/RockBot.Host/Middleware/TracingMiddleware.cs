using System.Diagnostics;
using RockBot.Messaging;

namespace RockBot.Host.Middleware;

/// <summary>
/// Outermost middleware that creates a distributed trace span for each message dispatch.
/// Extracts parent context from envelope headers via W3C TraceContext propagation.
/// </summary>
public sealed class TracingMiddleware : IMiddleware
{
    public async Task InvokeAsync(MessageHandlerContext context, MessageHandlerDelegate next)
    {
        var envelope = context.Envelope;

        // Extract parent trace context from envelope headers
        var parentContext = TraceContextPropagator.Extract(envelope.Headers);

        using var activity = HostDiagnostics.Source.StartActivity(
            $"dispatch {envelope.MessageType}",
            ActivityKind.Internal,
            parentContext ?? default);

        if (activity is not null)
        {
            activity.SetTag("rockbot.message_type", envelope.MessageType);
            activity.SetTag("messaging.message_id", envelope.MessageId);
            activity.SetTag("rockbot.agent", context.Agent.Name);

            if (envelope.CorrelationId is not null)
                activity.SetTag("rockbot.correlation_id", envelope.CorrelationId);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await next(context);
            sw.Stop();

            if (context.Result == MessageResult.DeadLetter)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Message dead-lettered");
                activity?.SetTag("rockbot.result", "dead_letter");
            }
            else
            {
                activity?.SetTag("rockbot.result", context.Result.ToString().ToLowerInvariant());
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("rockbot.result", "error");
            throw;
        }
        finally
        {
            HostDiagnostics.DispatchDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("rockbot.message_type", envelope.MessageType),
                new KeyValuePair<string, object?>("rockbot.result",
                    activity?.GetTagItem("rockbot.result") ?? "unknown"));
        }
    }
}
