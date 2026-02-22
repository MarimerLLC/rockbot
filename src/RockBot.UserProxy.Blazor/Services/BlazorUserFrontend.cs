namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Blazor implementation of IUserFrontend that updates chat state for real-time UI updates.
/// </summary>
public sealed class BlazorUserFrontend(ChatStateService chatState) : IUserFrontend
{
    public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        if (reply.IsFinal)
        {
            // Final result — add as a permanent chat bubble and clear the progress indicator
            chatState.SetThinkingMessage(null);
            chatState.AddAgentReply(reply);
        }
        else
        {
            // Intermediate progress — update thinking indicator AND add a bubble so
            // all agent traffic is visible for debugging.
            chatState.SetThinkingMessage(reply.Content);
            chatState.AddAgentReply(reply);
        }
        return Task.CompletedTask;
    }

    public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        chatState.AddError(message);
        return Task.CompletedTask;
    }
}
