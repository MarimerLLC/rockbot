namespace RockBot.UserProxy.AgUi;

/// <summary>
/// The proxy's <see cref="IUserFrontend"/>. A run's own traffic is correlated, so it reaches the
/// run's stream without passing through here. What does arrive here is everything nobody asked
/// for: subagent results, scheduled-task output, status changes. AG-UI has no place for it in a
/// run, and the notify stream that will carry it isn't built yet (#794, phase 2), so for now it is
/// logged and dropped.
/// </summary>
internal sealed class AgUiUserFrontend(ILogger<AgUiUserFrontend> logger) : IUserFrontend
{
    public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Dropping unsolicited reply from {Agent} for session {SessionId}: no notify stream yet",
            reply.AgentName, reply.SessionId);
        return Task.CompletedTask;
    }

    public Task DisplayStatusAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Dropping unsolicited status from {Agent} for session {SessionId}: no notify stream yet",
            reply.AgentName, reply.SessionId);
        return Task.CompletedTask;
    }

    public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Dropping error outside a run: {Message}", message);
        return Task.CompletedTask;
    }
}
