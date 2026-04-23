using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A;

/// <summary>
/// Cancels in-flight A2A tasks by <c>PrimarySessionId</c>. Invoked by wisp
/// failure handling (via the <see cref="ISessionA2ACanceller"/> seam) so that
/// a dispatched agent call doesn't keep running — and producing a duplicate
/// result — after the wisp that dispatched it aborts.
///
/// For each matching task:
/// <list type="number">
///   <item>Remove from the tracker (late results will then be ignored by
///     <c>A2ATaskResultHandler</c>'s unknown-correlationId path).</item>
///   <item>Cancel the local <see cref="CancellationTokenSource"/> so any
///     HTTP dispatch / polling loop unwinds.</item>
///   <item>Publish <see cref="AgentTaskCancelRequest"/> to
///     <c>agent.task.cancel.{TargetAgent}</c>. Receivers that implement
///     cancellation will stop work; stub handlers will respond with
///     <c>TaskNotCancelable</c> and we ignore it — removing locally is still
///     the correct caller-side behavior.</item>
/// </list>
/// </summary>
internal sealed class A2ATaskCanceller(
    A2ATaskTracker tracker,
    IMessagePublisher publisher,
    A2AOptions options,
    AgentIdentity identity,
    ILogger<A2ATaskCanceller> logger) : ISessionA2ACanceller
{
    public async Task<int> CancelForSessionAsync(string sessionId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId))
            return 0;

        var matches = tracker.ListBySession(sessionId);
        if (matches.Count == 0)
            return 0;

        logger.LogWarning(
            "Cancelling {Count} in-flight A2A task(s) for session {SessionId}: {Reason}",
            matches.Count, sessionId, reason);

        var cancelled = 0;
        foreach (var pending in matches)
        {
            // Drop from tracker first so a result arriving mid-cancel is treated
            // as an unknown-correlationId and ignored.
            tracker.TryRemove(pending.TaskId, out _);

            try
            {
                pending.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by a concurrent terminal path — benign.
            }

            var cancelRequest = new AgentTaskCancelRequest
            {
                TaskId = pending.TaskId,
                ContextId = pending.ContextId
            };
            var replyTo = $"{options.CallerResultTopic}.{identity.Name}";
            var envelope = cancelRequest.ToEnvelope<AgentTaskCancelRequest>(
                source: identity.Name,
                correlationId: pending.TaskId,
                replyTo: replyTo);

            try
            {
                await publisher.PublishAsync(
                    $"{options.CancelTopic}.{pending.TargetAgent}", envelope, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Failed to publish cancel for A2A task {TaskId} to agent '{Agent}' — local state dropped anyway",
                    pending.TaskId, pending.TargetAgent);
            }

            cancelled++;
        }

        return cancelled;
    }
}
