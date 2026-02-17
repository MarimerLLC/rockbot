using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Llm;

/// <summary>
/// Handles LLM requests by bridging the message bus to IChatClient.
/// Stateless transformer: request in, response out.
/// </summary>
internal sealed class LlmRequestHandler(
    IChatClient chatClient,
    IMessagePublisher publisher,
    LlmOptions options,
    AgentIdentity agent,
    ILogger<LlmRequestHandler> logger) : IMessageHandler<LlmRequest>
{
    public async Task HandleAsync(LlmRequest request, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo ?? options.DefaultResponseTopic;
        var correlationId = context.Envelope.CorrelationId;

        try
        {
            var messages = LlmMessageMapper.ToChatMessages(request.Messages);
            var chatOptions = LlmMessageMapper.ToChatOptions(request, options);

            logger.LogDebug("Sending LLM request with {MessageCount} messages to model {ModelId}",
                request.Messages.Count, chatOptions.ModelId ?? "(default)");

            var response = await chatClient.GetResponseAsync(
                messages, chatOptions, context.CancellationToken);

            var llmResponse = LlmMessageMapper.ToLlmResponse(response);
            var envelope = llmResponse.ToEnvelope<LlmResponse>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);

            logger.LogDebug("Published LLM response to {ReplyTo} (finish: {FinishReason})",
                replyTo, llmResponse.FinishReason);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw; // Host shutting down — don't swallow
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM provider error for correlation {CorrelationId}", correlationId);

            var error = LlmMessageMapper.ClassifyError(ex);
            var envelope = error.ToEnvelope<LlmError>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);
        }
    }
}
