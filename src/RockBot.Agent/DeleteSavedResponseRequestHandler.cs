using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="DeleteSavedResponseRequest"/> by removing a saved response from the store.
/// Deterministic — no LLM invocation.
/// </summary>
internal sealed class DeleteSavedResponseRequestHandler(
    ISavedResponseStore store,
    IMessagePublisher publisher,
    ILogger<DeleteSavedResponseRequestHandler> logger) : IMessageHandler<DeleteSavedResponseRequest>
{
    public async Task HandleAsync(DeleteSavedResponseRequest message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;

        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("DeleteSavedResponseRequest received with no replyTo — ignoring");
            return;
        }

        await store.DeleteAsync(message.Id, ct);

        var ack = new DeleteSavedResponseAck { Success = true };
        var envelope = ack.ToEnvelope<DeleteSavedResponseAck>(
            source: context.Agent.Name,
            correlationId: correlationId);

        await publisher.PublishAsync(replyTo, envelope, ct);

        logger.LogDebug("Deleted saved response '{Id}'", message.Id);
    }
}
