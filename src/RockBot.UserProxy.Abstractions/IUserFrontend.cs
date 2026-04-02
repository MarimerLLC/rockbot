namespace RockBot.UserProxy;

/// <summary>
/// Abstraction for displaying agent replies and errors to the user.
/// </summary>
public interface IUserFrontend
{
    Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default);
    Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when the agent's display name changes. Frontends should update
    /// their UI (titles, headers, etc.) accordingly.
    /// </summary>
    Task OnAgentNameChangedAsync(string agentName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
