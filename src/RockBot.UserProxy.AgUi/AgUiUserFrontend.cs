using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi;

/// <summary>
/// The proxy's <see cref="IUserFrontend"/>. A run's own traffic is correlated, so it reaches the
/// run's stream without passing through here. What does arrive here is everything nobody is
/// waiting for: subagent results, scheduled-task output, status changes, the late reply of a run
/// that timed out. AG-UI has no place for it in a run, so it goes to the notify stream
/// (<c>GET /agui/notify</c>): each callback is mapped to AG-UI events and published to the threads
/// it concerns.
/// </summary>
/// <remarks>
/// Called on the bus's message-handling thread, and not awaited, so every method finishes
/// synchronously, does no I/O and never throws. <see cref="NotifyHub"/> only queues.
/// </remarks>
internal sealed class AgUiUserFrontend(NotifyHub hub, ILogger<AgUiUserFrontend> logger) : IUserFrontend
{
    // ── Replies: routed by session ───────────────────────────────────────────

    public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default) =>
        Publish(nameof(DisplayReplyAsync), () => hub.PublishToSession(reply.SessionId, m => NotifyEvents.Reply(m, reply)));

    public Task DisplayStatusAsync(AgentReply reply, CancellationToken cancellationToken = default) =>
        Publish(nameof(DisplayStatusAsync), () => hub.PublishToSession(reply.SessionId, m => NotifyEvents.Status(m, reply)));

    public Task OnMcpQuestionStatusAsync(McpQuestionStatus status, CancellationToken cancellationToken = default) =>
        Publish(nameof(OnMcpQuestionStatusAsync),
            () => hub.PublishToSession(status.SessionId, m => [NotifyEvents.McpQuestionStatus(m, status)]));

    // ── Structured events: they carry the raw thread id ──────────────────────

    public Task OnSubagentStartedAsync(SubagentStarted started, CancellationToken cancellationToken = default) =>
        ToThread(nameof(OnSubagentStartedAsync), started.ThreadId, m => [m.SubagentStarted(started)]);

    public Task OnSubagentFinishedAsync(SubagentFinished finished, CancellationToken cancellationToken = default) =>
        ToThread(nameof(OnSubagentFinishedAsync), finished.ThreadId, m => NotifyEvents.SubagentFinished(m, finished));

    public Task OnTaskListSnapshotAsync(TaskListSnapshot snapshot, CancellationToken cancellationToken = default) =>
        ToThread(nameof(OnTaskListSnapshotAsync), snapshot.ThreadId, m => [m.TaskListSnapshot(snapshot)]);

    public Task OnTaskListDeltaAsync(TaskListDelta delta, CancellationToken cancellationToken = default) =>
        ToThread(nameof(OnTaskListDeltaAsync), delta.ThreadId, m => [m.TaskListDelta(delta)]);

    // Run events for a turn this proxy stopped waiting for (its run ended with rockbot.reply_pending).

    public Task OnRunStartedAsync(RunStarted run, CancellationToken cancellationToken = default) =>
        ToThread(nameof(OnRunStartedAsync), run.ThreadId, m => [NotifyEvents.RunStarted(m, run)]);

    public Task OnRunHeartbeatAsync(RunHeartbeat heartbeat, CancellationToken cancellationToken = default) =>
        ToThread(nameof(OnRunHeartbeatAsync), heartbeat.ThreadId, m => [NotifyEvents.RunHeartbeat(m, heartbeat)]);

    public Task OnRunFinishedAsync(RunFinished run, CancellationToken cancellationToken = default) =>
        ToThread(nameof(OnRunFinishedAsync), run.ThreadId, m => [NotifyEvents.RunFinished(m, run)]);

    // ── No session: every thread ─────────────────────────────────────────────

    public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default) =>
        ToAll(nameof(DisplayErrorAsync), m => [NotifyEvents.Error(m, message)]);

    public Task OnAgentNameChangedAsync(string agentName, CancellationToken cancellationToken = default) =>
        ToAll(nameof(OnAgentNameChangedAsync), m => [NotifyEvents.AgentName(m, agentName)]);

    public Task OnMcpSignInStatusAsync(McpSignInStatus status, CancellationToken cancellationToken = default) =>
        ToAll(nameof(OnMcpSignInStatusAsync), m => [NotifyEvents.McpSignInStatus(m, status)]);

    public Task OnActiveWorkSnapshotAsync(ActiveStatusResponse snapshot, CancellationToken cancellationToken = default) =>
        ToAll(nameof(OnActiveWorkSnapshotAsync), m => [NotifyEvents.ActiveWork(m, snapshot)]);

    private Task ToThread(string what, string? threadId, Func<TurnEventMapper, IEnumerable<BaseEvent>> map) =>
        Publish(what, () => hub.PublishToThread(threadId, map));

    private Task ToAll(string what, Func<TurnEventMapper, IEnumerable<BaseEvent>> map) =>
        Publish(what, () => hub.PublishToAll(map));

    private Task Publish(string what, Action publish)
    {
        try
        {
            publish();
        }
        catch (Exception ex)
        {
            // The proxy service doesn't observe most of these calls; a throw would be lost or,
            // worse, fail the message that carried the event.
            logger.LogWarning(ex, "Failed to put {What} on the notify stream", what);
        }
        return Task.CompletedTask;
    }
}
