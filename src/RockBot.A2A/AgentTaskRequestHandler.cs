using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A;

/// <summary>
/// Bus-facing handler for <see cref="AgentTaskRequest"/>. Receives requests from the bus,
/// validates them, delegates to <see cref="IAgentTaskHandler"/>, and publishes results.
/// </summary>
internal sealed class AgentTaskRequestHandler(
    IAgentTaskHandler taskHandler,
    IMessagePublisher publisher,
    A2AOptions options,
    AgentIdentity agent,
    ILogger<AgentTaskRequestHandler> logger) : IMessageHandler<AgentTaskRequest>
{
    public async Task HandleAsync(AgentTaskRequest request, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo ?? options.DefaultResultTopic;
        var correlationId = context.Envelope.CorrelationId;

        // Publish Working status update
        var statusUpdate = new AgentTaskStatusUpdate
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            State = AgentTaskState.Working
        };
        var statusEnvelope = statusUpdate.ToEnvelope<AgentTaskStatusUpdate>(
            source: agent.Name,
            correlationId: correlationId);
        await publisher.PublishAsync(options.StatusTopic, statusEnvelope, context.CancellationToken);

        try
        {
            var taskContext = new AgentTaskContext
            {
                MessageContext = context,
                PublishStatus = async (update, ct) =>
                {
                    var envelope = update.ToEnvelope<AgentTaskStatusUpdate>(
                        source: agent.Name,
                        correlationId: correlationId);
                    await publisher.PublishAsync(options.StatusTopic, envelope, ct);
                }
            };

            var result = await taskHandler.HandleTaskAsync(request, taskContext);

            var resultEnvelope = result.ToEnvelope<AgentTaskResult>(
                source: agent.Name,
                correlationId: correlationId);
            await publisher.PublishAsync(replyTo, resultEnvelope, context.CancellationToken);

            logger.LogDebug("Published task result for {TaskId} to {ReplyTo} (state: {State})",
                request.TaskId, replyTo, result.State);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Task handler failed for {TaskId}", request.TaskId);

            var error = new AgentTaskError
            {
                TaskId = request.TaskId,
                ContextId = request.ContextId,
                Code = AgentTaskError.Codes.ExecutionFailed,
                Message = ex.Message,
                IsRetryable = false
            };
            var errorEnvelope = error.ToEnvelope<AgentTaskError>(
                source: agent.Name,
                correlationId: correlationId);
            await publisher.PublishAsync(replyTo, errorEnvelope, context.CancellationToken);
        }
    }
}
