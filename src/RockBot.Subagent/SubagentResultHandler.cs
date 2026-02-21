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
    ILongTermMemory longTermMemory,
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
            "Subagent result for task {TaskId} in primary session {SessionId}: success={Success}, output={OutputLen} chars",
            message.TaskId, message.PrimarySessionId, message.IsSuccess, message.Output.Length);

        if (string.IsNullOrWhiteSpace(message.Output))
            logger.LogWarning("Subagent {TaskId} returned empty output — primary agent will have nothing to relay", message.TaskId);

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
        finally
        {
            // Clean up whiteboard entries now that the primary agent has processed the result.
            // The category 'subagent-whiteboards/{taskId}' is the agreed naming convention.
            await CleanupWhiteboardAsync(message.TaskId, CancellationToken.None);
        }
    }

    private async Task CleanupWhiteboardAsync(string taskId, CancellationToken ct)
    {
        try
        {
            var category = $"subagent-whiteboards/{taskId}";
            var entries = await longTermMemory.SearchAsync(
                new MemorySearchCriteria(Category: category, MaxResults: 100), ct);

            foreach (var entry in entries)
                await longTermMemory.DeleteAsync(entry.Id, ct);

            if (entries.Count > 0)
                logger.LogInformation(
                    "Cleaned up {Count} whiteboard entry(ies) for task {TaskId}",
                    entries.Count, taskId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up whiteboard for task {TaskId}", taskId);
        }
    }
}
