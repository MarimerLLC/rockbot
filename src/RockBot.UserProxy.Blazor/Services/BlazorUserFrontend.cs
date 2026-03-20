namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Blazor implementation of IUserFrontend that updates chat state for real-time UI updates.
/// </summary>
public sealed class BlazorUserFrontend(ChatStateService chatState) : IUserFrontend
{
    public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        var category = CategorizeReply(reply);

        if (reply.IsFinal)
        {
            // Final result — add as a permanent chat bubble and clear the progress indicator
            chatState.SetThinkingMessage(null);
            chatState.AddAgentReply(reply, category);
        }
        else
        {
            // Intermediate progress — update thinking indicator AND add a bubble so
            // all agent traffic is visible for debugging.
            chatState.SetThinkingMessage(reply.Content);
            chatState.AddAgentReply(reply, category);
        }
        return Task.CompletedTask;
    }

    public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        chatState.AddError(message);
        return Task.CompletedTask;
    }

    private static MessageCategory CategorizeReply(AgentReply reply)
    {
        if (reply.SessionId == "scheduled-system")
            return MessageCategory.ScheduledSystem;

        if (reply.SessionId == "scheduled")
            return MessageCategory.ScheduledUser;

        if (reply.AgentName?.StartsWith("subagent-", StringComparison.OrdinalIgnoreCase) == true)
            return reply.IsFinal ? MessageCategory.SubagentActivity : MessageCategory.SubagentActivity;

        if (reply.IsFinal)
            return MessageCategory.PrimaryFinal;

        // Non-final from a non-subagent, non-primary source — treat as A2A activity
        if (!string.IsNullOrEmpty(reply.AgentName))
            return MessageCategory.A2AActivity;

        return MessageCategory.PrimaryProgress;
    }
}
