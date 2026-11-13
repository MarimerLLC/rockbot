using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Subagent;
using RockBot.Subagent.Worker;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="ActiveStatusRequest"/> by returning a snapshot of currently
/// running subagents, workers and processing state. Lightweight and deterministic — no LLM invocation.
/// </summary>
internal sealed class ActiveStatusRequestHandler(
    ISubagentManager subagentManager,
    ISessionTracker sessionTracker,
    IMessagePublisher publisher,
    ILogger<ActiveStatusRequestHandler> logger,
    IWorkerManager? workerManager = null) : IMessageHandler<ActiveStatusRequest>
{
    /// <summary>The session older proxies' requests ask about (the Blazor client's fixed one).</summary>
    internal const string LegacySessionId = "blazor-session";

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

        var sessionId = string.IsNullOrWhiteSpace(message.SessionId) ? LegacySessionId : message.SessionId;
        var response = BuildSnapshot(subagentManager, workerManager, sessionTracker.HasActiveUserLoop(sessionId));

        var envelope = response.ToEnvelope<ActiveStatusResponse>(
            source: context.Agent.Name,
            correlationId: correlationId);

        await publisher.PublishAsync(replyTo, envelope, ct);

        logger.LogDebug("Published active status: {SubagentCount} subagents, {WorkCount} work items, isProcessing={IsProcessing}",
            response.Subagents.Count, response.Work?.Count ?? 0, response.IsProcessing);
    }

    /// <summary>The running subagents and workers, as an <see cref="ActiveStatusResponse"/>.</summary>
    internal static ActiveStatusResponse BuildSnapshot(
        ISubagentManager? subagentManager, IWorkerManager? workerManager, bool isProcessing)
    {
        var subagents = subagentManager?.ListActive() ?? [];
        var workers = workerManager?.ListActive() ?? [];

        return new ActiveStatusResponse
        {
            Subagents = subagents
                .Select(e => new ActiveSubagentInfo
                {
                    TaskId = e.TaskId,
                    Description = e.Description,
                    StartedAt = e.StartedAt
                })
                .ToList(),
            IsProcessing = isProcessing,
            Work =
            [
                .. subagents.Select(e => new ActiveWorkItem
                {
                    TaskId = e.TaskId,
                    Kind = ReplySourceKind.Subagent,
                    Description = e.Description,
                    ThreadId = SessionIds.Raw(e.PrimarySessionId),
                    StartedAt = e.StartedAt,
                    ParentToolCallId = e.ParentToolCallId,
                }),
                .. workers.Select(w => new ActiveWorkItem
                {
                    TaskId = w.TaskId,
                    Kind = ReplySourceKind.Worker,
                    Description = w.Description,
                    ThreadId = SessionIds.Raw(w.PrimarySessionId),
                    StartedAt = w.StartedAt,
                    ParentToolCallId = w.ParentToolCallId,
                }),
            ],
        };
    }
}
