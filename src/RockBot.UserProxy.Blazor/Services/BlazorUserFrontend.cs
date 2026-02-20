namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Blazor implementation of IUserFrontend that updates chat state for real-time UI updates.
/// </summary>
public sealed class BlazorUserFrontend(ChatStateService chatState) : IUserFrontend
{
    public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        chatState.AddAgentReply(reply);
        return Task.CompletedTask;
    }

    public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        chatState.AddError(message);
        return Task.CompletedTask;
    }
}
