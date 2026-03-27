using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="ListSavedResponsesRequest"/> by returning summaries of all saved responses.
/// Deterministic — no LLM invocation.
/// </summary>
internal sealed class ListSavedResponsesRequestHandler(
    ISavedResponseStore store,
    IMessagePublisher publisher,
    ILogger<ListSavedResponsesRequestHandler> logger) : IMessageHandler<ListSavedResponsesRequest>
{
    public async Task HandleAsync(ListSavedResponsesRequest message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;

        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("ListSavedResponsesRequest received with no replyTo — ignoring");
            return;
        }

        var all = await store.ListAsync(ct);
        var summaries = all.Select(r => new SavedResponseSummary(r.Id, r.Label, r.AgentName, r.SavedAt)).ToList();

        var response = new ListSavedResponsesResponse { Items = summaries };
        var envelope = response.ToEnvelope<ListSavedResponsesResponse>(
            source: context.Agent.Name,
            correlationId: correlationId);

        await publisher.PublishAsync(replyTo, envelope, ct);

        logger.LogDebug("Returned {Count} saved response summaries", summaries.Count);
    }
}
