using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.Subagent;

/// <summary>
/// Handles subagent progress messages on the primary agent side. Builds full primary agent
/// context and runs the LLM to incorporate the progress update into the conversation.
/// </summary>
#pragma warning disable CS9113 // Primary constructor parameters reserved for future handler expansion
internal sealed class SubagentProgressHandler(
    AgentLoopRunner agentLoopRunner,
    AgentContextBuilder agentContextBuilder,
    IMessagePublisher publisher,
    AgentIdentity agent,
    IWorkingMemory workingMemory,
    MemoryTools memoryTools,
    IToolRegistry toolRegistry,
    ToolGuideTools toolGuideTools,
    IConversationMemory conversationMemory,
    SessionOriginStore originStore,
    ILogger<SubagentProgressHandler> logger) : IMessageHandler<SubagentProgressMessage>
{
    public async Task HandleAsync(SubagentProgressMessage message, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;

        logger.LogInformation(
            "Subagent progress for task {TaskId} in primary session {SessionId}: {Message}",
            message.TaskId, message.PrimarySessionId, message.Message);

        // Subagent progress updates are ephemeral status indicators — relay directly
        // to the user without an LLM call. Running the LLM loop for each progress
        // message produces a second full chat bubble before the subagent even finishes,
        // causing results to appear twice in different formats.
        // Skip conversation memory too: progress turns pollute context the same way
        // A2A Working status updates do.
        try
        {
            const string SessionPrefix = "session/";
            var rawSessionId = message.PrimarySessionId.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
                ? message.PrimarySessionId[SessionPrefix.Length..]
                : message.PrimarySessionId;
            var progressReply = new AgentReply
            {
                Content = message.Message,
                SessionId = message.PrimarySessionId,
                AgentName = $"subagent-{message.TaskId}",
                IsFinal = false,
                Origin = originStore.Get(rawSessionId)
            };
            var envelope = progressReply.ToEnvelope<AgentReply>(source: agent.Name);
            await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to relay subagent progress for task {TaskId}", message.TaskId);
        }
    }
}
