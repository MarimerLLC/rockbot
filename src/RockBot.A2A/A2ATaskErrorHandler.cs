using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.A2A;

/// <summary>
/// Handles <see cref="AgentTaskError"/> messages from external agents.
/// Folds the error into the primary agent's LLM conversation.
/// </summary>
internal sealed class A2ATaskErrorHandler(
    AgentLoopRunner agentLoopRunner,
    AgentContextBuilder agentContextBuilder,
    IMessagePublisher publisher,
    AgentIdentity agent,
    IWorkingMemory workingMemory,
    MemoryTools memoryTools,
    IToolRegistry toolRegistry,
    ToolGuideTools toolGuideTools,
    IConversationMemory conversationMemory,
    A2ATaskTracker tracker,
    ILogger<A2ATaskErrorHandler> logger) : IMessageHandler<AgentTaskError>
{
    public async Task HandleAsync(AgentTaskError error, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;
        var correlationId = context.Envelope.CorrelationId;

        if (string.IsNullOrWhiteSpace(correlationId) || !tracker.TryRemove(correlationId, out var pending) || pending is null)
        {
            logger.LogDebug("Received AgentTaskError with correlationId={CorrelationId} — not tracked, ignoring", correlationId);
            return;
        }

        pending.Cts.Cancel();
        pending.Cts.Dispose();

        logger.LogWarning(
            "A2A task error for task {TaskId} from agent '{TargetAgent}' in session {SessionId}: [{Code}] {Message}",
            error.TaskId, pending.TargetAgent, pending.PrimarySessionId, error.Code, error.Message);

        var syntheticUserTurn = $"[Agent '{pending.TargetAgent}' failed task {error.TaskId} (code={error.Code})]: {error.Message}";

        await conversationMemory.AddTurnAsync(
            pending.PrimarySessionId,
            new ConversationTurn("user", syntheticUserTurn, DateTimeOffset.UtcNow),
            ct);

        var chatMessages = await agentContextBuilder.BuildAsync(
            pending.PrimarySessionId, syntheticUserTurn, ct);

        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, pending.PrimarySessionId, logger);
        var registryTools = toolRegistry.GetTools()
            .Select(r => (AIFunction)new RegistryToolFunction(
                r, toolRegistry.GetExecutor(r.Name)!, pending.PrimarySessionId))
            .ToArray();

        var chatOptions = new ChatOptions
        {
            Tools = [..memoryTools.Tools, ..sessionWorkingMemoryTools.Tools, ..toolGuideTools.Tools, ..registryTools]
        };

        try
        {
            var finalContent = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, pending.PrimarySessionId, cancellationToken: ct);

            await conversationMemory.AddTurnAsync(
                pending.PrimarySessionId,
                new ConversationTurn("assistant", finalContent, DateTimeOffset.UtcNow),
                ct);

            var reply = new AgentReply
            {
                Content = finalContent,
                SessionId = pending.PrimarySessionId,
                AgentName = agent.Name,
                IsFinal = true
            };
            var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
            await publisher.PublishAsync(UserProxyTopics.UserResponse, envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to handle A2A task error for task {TaskId}", error.TaskId);
        }
    }
}
