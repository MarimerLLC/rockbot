using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.A2A;

/// <summary>
/// Waits for in-flight A2A tasks by <c>PrimarySessionId</c>. Invoked by the
/// subagent runner (via the <see cref="ISessionA2AAwaiter"/> seam) so a
/// subagent that dispatched an A2A call doesn't publish its result while the
/// target agent is still working — otherwise the response, when it arrives,
/// is dropped by <c>A2ATaskResultHandler</c> because its subagent originator
/// has already exited and is treated as a non-user session with nobody left
/// to consume the working-memory result.
///
/// Termination signal: a task disappears from <see cref="A2ATaskTracker"/>
/// when <c>A2ATaskResultHandler</c> or <c>A2ATaskErrorHandler</c> calls
/// <c>TryRemove</c> on it (both terminal paths). Polling the tracker is
/// sufficient — no extra synchronization is needed.
/// </summary>
internal sealed class A2ATaskAwaiter(
    A2ATaskTracker tracker,
    ILogger<A2ATaskAwaiter> logger) : ISessionA2AAwaiter
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async Task<int> WaitForSessionAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId))
            return 0;

        var initial = tracker.ListBySession(sessionId);
        if (initial.Count == 0)
            return 0;

        logger.LogInformation(
            "Awaiting {Count} in-flight A2A task(s) for session {SessionId}: {TaskIds}",
            initial.Count, sessionId,
            string.Join(", ", initial.Select(t => $"{t.TargetAgent}:{t.TaskId}")));

        while (!ct.IsCancellationRequested)
        {
            if (tracker.ListBySession(sessionId).Count == 0)
            {
                logger.LogInformation(
                    "All A2A task(s) for session {SessionId} reached terminal state",
                    sessionId);
                return initial.Count;
            }

            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var remaining = tracker.ListBySession(sessionId).Count;
        if (remaining > 0)
        {
            logger.LogWarning(
                "Stopped awaiting A2A task(s) for session {SessionId} with {Remaining} still pending (cancelled or timed out)",
                sessionId, remaining);
        }
        return initial.Count;
    }
}
