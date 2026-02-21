using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Tools;

namespace RockBot.Subagent;

/// <summary>
/// Executes a single subagent task: builds focused context, runs the LLM tool loop,
/// publishes progress via report_progress tool, and publishes final result.
/// Resolved per-task from a DI scope.
/// </summary>
internal sealed class SubagentRunner(
    AgentLoopRunner agentLoopRunner,
    ILlmClient llmClient,
    IWorkingMemory workingMemory,
    MemoryTools memoryTools,
    ISkillStore skillStore,
    IToolRegistry toolRegistry,
    IMessagePublisher publisher,
    AgentIdentity agentIdentity,
    ILogger<SubagentRunner> logger)
{
    public async Task RunAsync(
        string taskId,
        string subagentSessionId,
        string description,
        string? context,
        string primarySessionId,
        CancellationToken ct)
    {
        logger.LogInformation(
            "Subagent {TaskId} starting (session {SessionId})", taskId, subagentSessionId);

        var systemPrompt =
            "You are a subagent executing a specific background task. " +
            "Use your available tools to complete the work. " +
            "Call ReportProgress periodically to send updates back to the primary agent. " +
            $"To share structured data with the primary agent, save it to long-term memory " +
            $"using the category 'subagent-whiteboards/{taskId}' — the primary agent will " +
            $"read it from there using SearchMemory or ListCategories. " +
            "Produce your final answer as the last message.";

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt)
        };

        if (!string.IsNullOrEmpty(context))
            chatMessages.Add(new ChatMessage(ChatRole.System, $"Context: {context}"));

        chatMessages.Add(new ChatMessage(ChatRole.User, description));

        // Long-term memory tools (search_memory, save_memory, etc.)
        // MemoryTools is a singleton — safe to use directly.

        // Skill tools (get_skill, list_skills, save_skill) — no usage tracking needed for subagents
        var skillTools = new SkillTools(skillStore, llmClient, logger, subagentSessionId);

        // Working memory tools scoped to the subagent's session
        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, subagentSessionId, logger);

        // Registry tools — include MCP data tools and web/script tools;
        // exclude subagent management, MCP infrastructure management, and scheduling
        // (subagents execute a specific task — they should not spawn other subagents,
        //  reconfigure MCP servers, or schedule new cron jobs).
        var registryTools = toolRegistry.GetTools()
            .Where(r => r.Source != "subagent"
                     && r.Source != "mcp:management"
                     && r.Source != "scheduling")
            .Select(r => (AIFunction)new SubagentRegistryToolFunction(
                r, toolRegistry.GetExecutor(r.Name)!, subagentSessionId))
            .ToArray();

        // report_progress tool — baked with taskId and primarySessionId
        var reportProgressFunctions = new ReportProgressFunctions(
            taskId, primarySessionId, publisher, agentIdentity, logger);

        var chatOptions = new ChatOptions
        {
            Tools = [
                ..memoryTools.Tools,
                ..sessionWorkingMemoryTools.Tools,
                ..skillTools.Tools,
                ..registryTools,
                ..reportProgressFunctions.Tools
            ]
        };

        string finalOutput;
        bool isSuccess;
        string? error = null;

        try
        {
            finalOutput = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, subagentSessionId,
                cancellationToken: ct);
            isSuccess = true;
        }
        catch (OperationCanceledException)
        {
            throw; // propagate cancellation
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subagent {TaskId} tool loop failed", taskId);
            finalOutput = $"Task failed: {ex.Message}";
            isSuccess = false;
            error = ex.Message;
        }

        // Publish result
        var result = new SubagentResultMessage
        {
            TaskId = taskId,
            SubagentSessionId = subagentSessionId,
            PrimarySessionId = primarySessionId,
            Output = finalOutput,
            IsSuccess = isSuccess,
            Error = error,
            Timestamp = DateTimeOffset.UtcNow
        };

        var envelope = result.ToEnvelope<SubagentResultMessage>(source: agentIdentity.Name);
        await publisher.PublishAsync(SubagentTopics.Result, envelope, CancellationToken.None);

        logger.LogInformation("Subagent {TaskId} published result (success={Success})", taskId, isSuccess);
    }
}
