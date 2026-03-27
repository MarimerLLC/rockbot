using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="GetSavedResponseRequest"/> by returning the full saved response content.
/// Deterministic — no LLM invocation.
/// </summary>
internal sealed class GetSavedResponseRequestHandler(
    ISavedResponseStore store,
    IMessagePublisher publisher,
    ILogger<GetSavedResponseRequestHandler> logger) : IMessageHandler<GetSavedResponseRequest>
{
    public async Task HandleAsync(GetSavedResponseRequest message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;

        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("GetSavedResponseRequest received with no replyTo — ignoring");
            return;
        }

        var saved = await store.GetAsync(message.Id, ct);

        GetSavedResponseResponse response;
        if (saved is not null)
        {
            response = new GetSavedResponseResponse
            {
                Id = saved.Id,
                Label = saved.Label,
                Content = saved.Content,
                AgentName = saved.AgentName,
                SavedAt = saved.SavedAt,
                Found = true
            };
        }
        else
        {
            response = new GetSavedResponseResponse
            {
                Id = message.Id,
                Label = string.Empty,
                Content = string.Empty,
                AgentName = string.Empty,
                SavedAt = default,
                Found = false
            };
        }

        var envelope = response.ToEnvelope<GetSavedResponseResponse>(
            source: context.Agent.Name,
            correlationId: correlationId);

        await publisher.PublishAsync(replyTo, envelope, ct);

        logger.LogDebug("Returned saved response '{Id}' (found={Found})", message.Id, response.Found);
    }
}
