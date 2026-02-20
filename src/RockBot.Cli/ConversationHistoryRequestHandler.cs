using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Cli;

/// <summary>
/// Handles <see cref="ConversationHistoryRequest"/> by reading turns from
/// <see cref="IConversationMemory"/> and publishing a <see cref="ConversationHistoryResponse"/>
/// back to the reply topic.
/// </summary>
internal sealed class ConversationHistoryRequestHandler(
    IConversationMemory conversationMemory,
    IMessagePublisher publisher,
    ILogger<ConversationHistoryRequestHandler> logger) : IMessageHandler<ConversationHistoryRequest>
{
    public async Task HandleAsync(ConversationHistoryRequest message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;

        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("ConversationHistoryRequest received with no replyTo — ignoring");
            return;
        }

        logger.LogDebug("Fetching conversation history for session {SessionId}", message.SessionId);

        var turns = await conversationMemory.GetTurnsAsync(message.SessionId, ct);

        var response = new ConversationHistoryResponse
        {
            Turns = turns
                .Select(t => new ConversationHistoryTurn
                {
                    Role = t.Role,
                    Content = t.Content,
                    Timestamp = t.Timestamp
                })
                .ToList()
        };

        var envelope = response.ToEnvelope<ConversationHistoryResponse>(
            source: context.Agent.Name,
            correlationId: correlationId,
            replyTo: null,
            destination: null);

        await publisher.PublishAsync(replyTo, envelope, ct);

        logger.LogDebug("Published {TurnCount} history turns for session {SessionId}",
            response.Turns.Count, message.SessionId);
    }
}
