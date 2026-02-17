using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Tools;

/// <summary>
/// Handles tool invocation requests by dispatching to the correct executor via the registry.
/// Stateless transformer: request in, response out.
/// </summary>
internal sealed class ToolInvokeHandler(
    IToolRegistry registry,
    IMessagePublisher publisher,
    ToolOptions options,
    AgentIdentity agent,
    ILogger<ToolInvokeHandler> logger) : IMessageHandler<ToolInvokeRequest>
{
    public async Task HandleAsync(ToolInvokeRequest request, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo ?? options.DefaultResultTopic;
        var correlationId = context.Envelope.CorrelationId;

        var executor = registry.GetExecutor(request.ToolName);
        if (executor is null)
        {
            logger.LogWarning("Tool not found: {ToolName}", request.ToolName);

            var error = new ToolError
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Code = ToolError.Codes.ToolNotFound,
                Message = $"Tool '{request.ToolName}' is not registered.",
                IsRetryable = false
            };
            var errorEnvelope = error.ToEnvelope<ToolError>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, errorEnvelope, context.CancellationToken);
            return;
        }

        try
        {
            logger.LogDebug("Invoking tool {ToolName} (call {ToolCallId})",
                request.ToolName, request.ToolCallId);

            var response = await executor.ExecuteAsync(request, context.CancellationToken);
            var envelope = response.ToEnvelope<ToolInvokeResponse>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);

            logger.LogDebug("Published tool result for {ToolName} to {ReplyTo}",
                request.ToolName, replyTo);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw; // Host shutting down — don't swallow
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool execution failed for {ToolName} (call {ToolCallId})",
                request.ToolName, request.ToolCallId);

            var error = ClassifyError(ex, request);
            var errorEnvelope = error.ToEnvelope<ToolError>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, errorEnvelope, context.CancellationToken);
        }
    }

    private static ToolError ClassifyError(Exception ex, ToolInvokeRequest request) => ex switch
    {
        TimeoutException => new ToolError
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Code = ToolError.Codes.Timeout,
            Message = ex.Message,
            IsRetryable = true
        },
        ArgumentException => new ToolError
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Code = ToolError.Codes.InvalidArguments,
            Message = ex.Message,
            IsRetryable = false
        },
        _ => new ToolError
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Code = ToolError.Codes.ExecutionFailed,
            Message = ex.Message,
            IsRetryable = false
        }
    };
}
