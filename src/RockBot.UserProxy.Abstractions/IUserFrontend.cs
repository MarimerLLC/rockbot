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
}
