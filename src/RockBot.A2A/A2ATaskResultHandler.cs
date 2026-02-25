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
    ILlmClient llmClient,
    IMessagePublisher publisher,
    AgentIdentity agent,
    IWorkingMemory workingMemory,
    MemoryTools memoryTools,
    ISkillStore skillStore,
    IToolRegistry toolRegistry,
    RulesTools rulesTools,
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

        // PrimarySessionId is the full WM session namespace (e.g. "session/blazor-session"),
        // as populated by InvokeAgentExecutor from the RegistryToolFunction's sessionId parameter.
        // Use it directly as the namespace; derive the raw session ID by stripping the prefix so
        // AgentContextBuilder, conversation memory, and skill tools all use consistent keys.
        var sessionNamespace = pending.PrimarySessionId;
        const string SessionPrefix = "session/";
        var rawSessionId = sessionNamespace.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
            ? sessionNamespace[SessionPrefix.Length..]
            : sessionNamespace;

        // Purge any previous result entries for this agent before storing the new one.
        // Old entries linger in working memory (60-min TTL) and the LLM will find them
        // in conversation history instructions — causing it to retrieve stale data instead
        // of the current result when both share the same key pattern.
        var staleAgentPattern = $"/a2a/{pending.TargetAgent}/";
        var staleEntries = await workingMemory.ListAsync(sessionNamespace);
        foreach (var entry in staleEntries)
        {
            if (entry.Key.Contains(staleAgentPattern, StringComparison.OrdinalIgnoreCase) &&
                entry.Key.EndsWith("/result", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Purging stale A2A result entry '{Key}' before storing new result", entry.Key);
                await workingMemory.DeleteAsync(entry.Key);
            }
        }

        // Always store A2A results in working memory so the LLM can reliably retrieve them
        // via get_from_working_memory regardless of result size. Storing inline in the
        // synthetic user turn caused the LLM to search WM, find nothing, and conclude the
        // result was unavailable — even though it was present in conversation history.
        var memoryKey = $"{sessionNamespace}/a2a/{pending.TargetAgent}/{result.TaskId}/result";
        await workingMemory.SetAsync(
            memoryKey,
            resultText,
            ttl: TimeSpan.FromMinutes(60),
            category: "a2a-result",
            tags: [pending.TargetAgent, result.TaskId]);

        logger.LogInformation(
            "A2A result for task {TaskId} ({Len:N0} chars) stored in working memory at key '{Key}'",
            result.TaskId, resultText.Length, memoryKey);

        syntheticUserTurn =
            $"[Agent '{pending.TargetAgent}' completed task {result.TaskId} (state={result.State})]: " +
            $"The result ({resultText.Length:N0} chars) is in working memory. " +
            $"Call get_from_working_memory with key '{memoryKey}' to read it before responding.";

        // Publish the agent's raw completion output as a non-final bubble so it is
        // visible in the Blazor UI under the agent's own name before the primary agent
        // synthesises and presents the final reply.
        try
        {
            const int PreviewMax = 500;
            var previewText = resultText.Length > PreviewMax
                ? resultText[..PreviewMax] + $"\n\n…({resultText.Length - PreviewMax:N0} more chars in working memory)"
                : resultText;
            var completionReply = new AgentReply
            {
                Content = previewText,
                SessionId = pending.PrimarySessionId,
                AgentName = pending.TargetAgent,
                IsFinal = false
            };
            var completionEnvelope = completionReply.ToEnvelope<AgentReply>(source: pending.TargetAgent);
            await publisher.PublishAsync(UserProxyTopics.UserResponse, completionEnvelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish completion bubble for A2A task {TaskId}", result.TaskId);
        }

        await conversationMemory.AddTurnAsync(
            rawSessionId,
            new ConversationTurn("user", syntheticUserTurn, DateTimeOffset.UtcNow),
            ct);

        var chatMessages = await agentContextBuilder.BuildAsync(
            rawSessionId, syntheticUserTurn, ct);

        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, sessionNamespace, logger);
        var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, rawSessionId);
        var registryTools = toolRegistry.GetTools()
            .Select(r => (AIFunction)new RegistryToolFunction(
                r, toolRegistry.GetExecutor(r.Name)!, sessionNamespace))
            .ToArray();

        var chatOptions = new ChatOptions
        {
            Tools = [..memoryTools.Tools, ..sessionWorkingMemoryTools.Tools, ..sessionSkillTools.Tools,
                     ..rulesTools.Tools, ..toolGuideTools.Tools, ..registryTools]
        };

        try
        {
            var finalContent = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, rawSessionId, cancellationToken: ct);

            await conversationMemory.AddTurnAsync(
                rawSessionId,
                new ConversationTurn("assistant", finalContent, DateTimeOffset.UtcNow),
                ct);

            var reply = new AgentReply
            {
                Content = finalContent,
                SessionId = rawSessionId,
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
