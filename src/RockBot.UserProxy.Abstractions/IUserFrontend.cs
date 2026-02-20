namespace RockBot.UserProxy;

/// <summary>
/// Abstraction for displaying agent replies and errors to the user.
/// </summary>
public interface IUserFrontend
{
    Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default);
    Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default);
}
