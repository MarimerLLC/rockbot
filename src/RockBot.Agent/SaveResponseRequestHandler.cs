using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="SaveResponseRequest"/> by persisting the response to the saved-response store.
/// Deterministic — no LLM invocation.
/// </summary>
internal sealed class SaveResponseRequestHandler(
    ISavedResponseStore store,
    IMessagePublisher publisher,
    ILogger<SaveResponseRequestHandler> logger) : IMessageHandler<SaveResponseRequest>
{
    public async Task HandleAsync(SaveResponseRequest message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;

        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("SaveResponseRequest received with no replyTo — ignoring");
            return;
        }

        var saved = new SavedResponse(
            Id: Guid.NewGuid().ToString("N"),
            Label: message.Label,
            Content: message.Content,
            AgentName: message.AgentName,
            SessionId: message.SessionId,
            SavedAt: DateTimeOffset.UtcNow);

        await store.SaveAsync(saved, ct);

        var ack = new SaveResponseAck { Id = saved.Id, Success = true };
        var envelope = ack.ToEnvelope<SaveResponseAck>(
            source: context.Agent.Name,
            correlationId: correlationId);

        await publisher.PublishAsync(replyTo, envelope, ct);

        logger.LogDebug("Saved response '{Id}' with label '{Label}'", saved.Id, saved.Label);
    }
}
