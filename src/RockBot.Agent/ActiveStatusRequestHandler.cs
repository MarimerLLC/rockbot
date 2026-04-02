using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Subagent;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="ActiveStatusRequest"/> by returning a snapshot of currently
/// running subagents and processing state. Lightweight and deterministic — no LLM invocation.
/// </summary>
internal sealed class ActiveStatusRequestHandler(
    ISubagentManager subagentManager,
    ISessionTracker sessionTracker,
    IMessagePublisher publisher,
    ILogger<ActiveStatusRequestHandler> logger) : IMessageHandler<ActiveStatusRequest>
{
    public async Task HandleAsync(ActiveStatusRequest message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;

        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("ActiveStatusRequest received with no replyTo — ignoring");
            return;
        }

        var activeSubagents = subagentManager.ListActive();

        var response = new ActiveStatusResponse
        {
            Subagents = activeSubagents
                .Select(e => new ActiveSubagentInfo
                {
                    TaskId = e.TaskId,
                    Description = e.Description,
                    StartedAt = e.StartedAt
                })
                .ToList(),
            IsProcessing = sessionTracker.HasActiveUserLoop("blazor-session")
        };

        var envelope = response.ToEnvelope<ActiveStatusResponse>(
            source: context.Agent.Name,
            correlationId: correlationId);

        await publisher.PublishAsync(replyTo, envelope, ct);

        logger.LogDebug("Published active status: {SubagentCount} subagents, isProcessing={IsProcessing}",
            response.Subagents.Count, response.IsProcessing);
    }
}
