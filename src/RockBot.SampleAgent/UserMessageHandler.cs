using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.SampleAgent;

/// <summary>
/// Handles incoming <see cref="UserMessage"/> by calling the LLM and publishing
/// an <see cref="AgentReply"/> back to the user.
/// </summary>
internal sealed class UserMessageHandler(
    IChatClient chatClient,
    IMessagePublisher publisher,
    AgentIdentity agent,
    AgentProfile profile,
    ISystemPromptBuilder promptBuilder,
    ILogger<UserMessageHandler> logger) : IMessageHandler<UserMessage>
{
    public async Task HandleAsync(UserMessage message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo ?? UserProxyTopics.UserResponse;
        var correlationId = context.Envelope.CorrelationId;

        logger.LogInformation("Received message from {UserId} in session {SessionId}: {Content}",
            message.UserId, message.SessionId, message.Content);

        try
        {
            var systemPrompt = promptBuilder.Build(profile, agent);
            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, message.Content)
            };

            var response = await chatClient.GetResponseAsync(
                chatMessages, cancellationToken: context.CancellationToken);

            var content = response.Text ?? string.Empty;

            var reply = new AgentReply
            {
                Content = content,
                SessionId = message.SessionId,
                AgentName = agent.Name,
                IsFinal = true
            };

            var envelope = reply.ToEnvelope<AgentReply>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);

            logger.LogInformation("Published reply to {ReplyTo} for correlation {CorrelationId}",
                replyTo, correlationId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process user message {CorrelationId}", correlationId);

            var errorReply = new AgentReply
            {
                Content = $"Sorry, I encountered an error: {ex.Message}",
                SessionId = message.SessionId,
                AgentName = agent.Name,
                IsFinal = true
            };

            var envelope = errorReply.ToEnvelope<AgentReply>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);
        }
    }
}
