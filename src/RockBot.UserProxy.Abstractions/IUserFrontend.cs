namespace RockBot.UserProxy;

/// <summary>
/// Abstraction for displaying agent replies and errors to the user.
/// </summary>
public interface IUserFrontend
{
    Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default);
    Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Displays a non-final, ephemeral progress update from an agent (e.g. subagent
    /// progress notes, A2A task status). These are unsolicited <see cref="AgentReply"/>
    /// envelopes that arrived without a matching pending request and have
    /// <see cref="AgentReply.IsFinal"/> = false. Frontends should render them as
    /// transient activity (e.g. an activity log line, a dim status row) rather than
    /// as a separate chat bubble per message, so progress does not stack as apparent
    /// duplicate output before the final reply arrives.
    ///
    /// The default implementation delegates to <see cref="DisplayReplyAsync"/> so
    /// existing custom frontends keep their previous behavior until they opt in.
    /// </summary>
    Task DisplayStatusAsync(AgentReply reply, CancellationToken cancellationToken = default) =>
        DisplayReplyAsync(reply, cancellationToken);

    /// <summary>
    /// Called when the agent's display name changes. Frontends should update
    /// their UI (titles, headers, etc.) accordingly.
    /// </summary>
    Task OnAgentNameChangedAsync(string agentName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Called when a handed-back MCP question shown as an <see cref="McpQuestionForm"/> changes
    /// state (answered, declined, expired, rejected…). Frontends that render the form update it;
    /// others ignore it, since the agent's own reply tells the user what happened.
    /// </summary>
    Task OnMcpQuestionStatusAsync(McpQuestionStatus status, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Called when an MCP server's OAuth sign-in changes: it now needs the user to sign in, or a
    /// sign-in finished, failed or expired. Frontends with a sign-in prompt show or clear it;
    /// others ignore it, since a tool call to the server says it needs a sign-in anyway.
    /// </summary>
    Task OnMcpSignInStatusAsync(McpSignInStatus status, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    // ── Structured agent events (#795) ──────────────────────────────────────────
    // A run event or task list for a turn whose SendTurnAsync is still waiting goes to that
    // turn's observer; these methods get the ones that arrive after it gave up, or for a turn
    // sent some other way.

    /// <summary>A run started that no pending turn is waiting for.</summary>
    Task OnRunStartedAsync(RunStarted run, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>A heartbeat for a run no pending turn is waiting for.</summary>
    Task OnRunHeartbeatAsync(RunHeartbeat heartbeat, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>A run finished that no pending turn is waiting for.</summary>
    Task OnRunFinishedAsync(RunFinished run, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>A subagent or worker started.</summary>
    Task OnSubagentStartedAsync(SubagentStarted started, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>A subagent or worker finished.</summary>
    Task OnSubagentFinishedAsync(SubagentFinished finished, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>The agent broadcast a snapshot of its running background work (for example, after it restarted).</summary>
    Task OnActiveWorkSnapshotAsync(ActiveStatusResponse snapshot, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>A task list was created or replaced.</summary>
    Task OnTaskListSnapshotAsync(TaskListSnapshot snapshot, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>One item of a task list changed.</summary>
    Task OnTaskListDeltaAsync(TaskListDelta delta, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
