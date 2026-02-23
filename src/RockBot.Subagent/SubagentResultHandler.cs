using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Llm;
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
    ILlmClient llmClient,
    IMessagePublisher publisher,
    AgentIdentity agent,
    IWorkingMemory workingMemory,
    ISharedMemory sharedMemory,
    MemoryTools memoryTools,
    SharedMemoryTools sharedMemoryTools,
    ISkillStore skillStore,
    IToolRegistry toolRegistry,
    RulesTools rulesTools,
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

        // The subagent's final text is the primary result. Large data (reports, lists, documents)
        // may have been written to shared memory using SaveToSharedMemory with category
        // "subagent-output". Only tell the LLM to retrieve from shared memory if entries
        // actually exist — an unconditional hint causes the LLM to call get_from_shared_memory
        // and conclude the data "expired" when nothing was ever written there.
        var sharedEntries = await sharedMemory.SearchAsync(
            new MemorySearchCriteria(Category: "subagent-output"));
        var relevantEntries = sharedEntries
            .Where(e => e.Key.Contains(message.TaskId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sharedHint = relevantEntries.Count > 0
            ? $" Additional outputs were written to shared memory. Keys: {string.Join(", ", relevantEntries.Select(e => $"'{e.Key}'"))}. Use get_from_shared_memory to retrieve and present them to the user."
            : string.Empty;

        // If the subagent ran out of iterations its final text may be an incomplete setup
        // phrase ("Now let me save the findings to shared memory:"). Annotate it so the
        // primary LLM knows no data was actually written — prevents hallucinated
        // "working memory expired" responses.
        var safeOutput = message.IsSuccess && AgentLoopRunner.IsIncompleteSetupPhrase(message.Output)
            ? message.Output.TrimEnd(':').TrimEnd() +
              " — but the task ran out of steps before completing this action. No data was saved to shared memory."
            : message.Output;

        // Publish the subagent's raw completion output as a non-final bubble so it is
        // visible in the Blazor UI under the subagent's own name before the primary agent
        // synthesises and presents the final reply.
        try
        {
            var completionContent = message.IsSuccess
                ? safeOutput
                : $"Task failed: {message.Error}\n\n{message.Output}";
            var completionReply = new AgentReply
            {
                Content = completionContent,
                SessionId = message.PrimarySessionId,
                AgentName = $"subagent-{message.TaskId}",
                IsFinal = false
            };
            var completionEnvelope = completionReply.ToEnvelope<AgentReply>(
                source: $"subagent-{message.TaskId}");
            await publisher.PublishAsync(UserProxyTopics.UserResponse, completionEnvelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish completion bubble for subagent {TaskId}", message.TaskId);
        }

        var syntheticUserTurn = message.IsSuccess
            ? $"[Subagent task {message.TaskId} completed]: {safeOutput}{sharedHint}"
            : $"[Subagent task {message.TaskId} completed with error: {message.Error}]: {message.Output}";

        await conversationMemory.AddTurnAsync(
            message.PrimarySessionId,
            new ConversationTurn("user", syntheticUserTurn, DateTimeOffset.UtcNow),
            ct);

        var chatMessages = await agentContextBuilder.BuildAsync(
            message.PrimarySessionId, syntheticUserTurn, ct);

        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, message.PrimarySessionId, logger);
        var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, message.PrimarySessionId);
        var registryTools = toolRegistry.GetTools()
            .Select(r => (AIFunction)new SubagentRegistryToolFunction(
                r, toolRegistry.GetExecutor(r.Name)!, message.PrimarySessionId))
            .ToArray();

        var chatOptions = new ChatOptions
        {
            Tools = [..memoryTools.Tools, ..sessionWorkingMemoryTools.Tools, ..sharedMemoryTools.Tools,
                     ..sessionSkillTools.Tools, ..rulesTools.Tools, ..toolGuideTools.Tools, ..registryTools]
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
