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
/// Handles subagent result messages on the primary agent side.
/// </summary>
internal sealed class SubagentResultHandler(
    AgentLoopRunner agentLoopRunner,
    AgentContextBuilder agentContextBuilder,
    IMessagePublisher publisher,
    AgentIdentity agent,
    IWorkingMemory workingMemory,
    MemoryTools memoryTools,
    IToolRegistry toolRegistry,
    ToolGuideTools toolGuideTools,
    IConversationMemory conversationMemory,
    ILogger<SubagentResultHandler> logger) : IMessageHandler<SubagentResultMessage>
{
    public async Task HandleAsync(SubagentResultMessage message, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;

        logger.LogInformation(
            "Subagent result for task {TaskId} in primary session {SessionId}: success={Success}",
            message.TaskId, message.PrimarySessionId, message.IsSuccess);

        var syntheticUserTurn = message.IsSuccess
            ? $"[Subagent task {message.TaskId} completed]: {message.Output}"
            : $"[Subagent task {message.TaskId} completed with error: {message.Error}]: {message.Output}";

        await conversationMemory.AddTurnAsync(
            message.PrimarySessionId,
            new ConversationTurn("user", syntheticUserTurn, DateTimeOffset.UtcNow),
            ct);

        var chatMessages = await agentContextBuilder.BuildAsync(
            message.PrimarySessionId, syntheticUserTurn, ct);

        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, message.PrimarySessionId, logger);
        var registryTools = toolRegistry.GetTools()
            .Select(r => (AIFunction)new SubagentRegistryToolFunction(
                r, toolRegistry.GetExecutor(r.Name)!, message.PrimarySessionId))
            .ToArray();

        var chatOptions = new ChatOptions
        {
            Tools = [..memoryTools.Tools, ..sessionWorkingMemoryTools.Tools, ..toolGuideTools.Tools, ..registryTools]
        };

        try
        {
            var finalContent = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, message.PrimarySessionId, cancellationToken: ct);

            await conversationMemory.AddTurnAsync(
                message.PrimarySessionId,
                new ConversationTurn("assistant", finalContent, DateTimeOffset.UtcNow),
                ct);

            var reply = new AgentReply
            {
                Content = finalContent,
                SessionId = message.PrimarySessionId,
                AgentName = agent.Name,
                IsFinal = true
            };
            var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
            await publisher.PublishAsync(UserProxyTopics.UserResponse, envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to handle subagent result for task {TaskId}", message.TaskId);
        }
    }
}
