using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.A2A;

/// <summary>
/// Handles <see cref="AgentTaskResult"/> messages from external agents.
/// Folds the result into the primary agent's LLM conversation.
/// </summary>
internal sealed class A2ATaskResultHandler(
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
    ModelBehavior modelBehavior,
    ILogger<A2ATaskResultHandler> logger) : IMessageHandler<AgentTaskResult>
{
    public async Task HandleAsync(AgentTaskResult result, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;
        var correlationId = context.Envelope.CorrelationId;

        if (string.IsNullOrWhiteSpace(correlationId) || !tracker.TryRemove(correlationId, out var pending) || pending is null)
        {
            logger.LogDebug("Received AgentTaskResult with correlationId={CorrelationId} — not tracked, ignoring", correlationId);
            return;
        }

        pending.Cts.Cancel();
        pending.Cts.Dispose();

        logger.LogInformation(
            "A2A task result for task {TaskId} from agent '{TargetAgent}' in session {SessionId} (state={State})",
            result.TaskId, pending.TargetAgent, pending.PrimarySessionId, result.State);

        var resultText = result.Message?.Parts.FirstOrDefault(p => p.Kind == "text")?.Text ?? "(no text output)";
        string syntheticUserTurn;

        if (resultText.Length > modelBehavior.ToolResultChunkingThreshold)
        {
            // Result is large — store it in working memory rather than injecting it raw into
            // conversation history. Raw injection would pollute every subsequent LLM call with
            // the full text. Instead the LLM retrieves it on demand via get_from_working_memory.
            var memoryKey = $"a2a:{pending.TargetAgent}:{result.TaskId}:result";
            await workingMemory.SetAsync(
                pending.PrimarySessionId,
                memoryKey,
                resultText,
                ttl: TimeSpan.FromMinutes(60),
                category: "a2a-result",
                tags: [pending.TargetAgent, result.TaskId]);

            logger.LogInformation(
                "A2A result for task {TaskId} is large ({Len:N0} chars); stored in working memory at key '{Key}'",
                result.TaskId, resultText.Length, memoryKey);

            syntheticUserTurn =
                $"[Agent '{pending.TargetAgent}' completed task {result.TaskId} (state={result.State})]: " +
                $"The result is large ({resultText.Length:N0} chars) and has been stored in working memory. " +
                $"Call get_from_working_memory with key '{memoryKey}' to read it before responding.";
        }
        else
        {
            syntheticUserTurn = $"[Agent '{pending.TargetAgent}' completed task {result.TaskId} (state={result.State})]: {resultText}";
        }

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
            logger.LogError(ex, "Failed to handle A2A task result for task {TaskId}", result.TaskId);
        }
    }
}
