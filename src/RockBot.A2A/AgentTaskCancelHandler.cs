using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A;

/// <summary>
/// Handles <see cref="AgentTaskCancelRequest"/> messages. Stateless agents don't track
/// in-flight tasks, so this always responds with TaskNotCancelable. Provides the message
/// type and topic wiring for future cancellation support.
/// </summary>
internal sealed class AgentTaskCancelHandler(
    IMessagePublisher publisher,
    A2AOptions options,
    AgentIdentity agent,
    ILogger<AgentTaskCancelHandler> logger) : IMessageHandler<AgentTaskCancelRequest>
{
    public async Task HandleAsync(AgentTaskCancelRequest request, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo ?? options.DefaultResultTopic;
        var correlationId = context.Envelope.CorrelationId;

        logger.LogDebug("Cancel request for task {TaskId} — not cancelable (stateless agent)",
            request.TaskId);

        var error = new AgentTaskError
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            Code = AgentTaskError.Codes.TaskNotCancelable,
            Message = "Stateless agents do not support task cancellation.",
            IsRetryable = false
        };
        var envelope = error.ToEnvelope<AgentTaskError>(
            source: agent.Name,
            correlationId: correlationId);
        await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);
    }
}
