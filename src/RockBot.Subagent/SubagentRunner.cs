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
    ToolGuideTools toolGuideTools,
    IMessagePublisher publisher,
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

        var subagentNamespace = $"subagent/{taskId}";
        var systemPrompt =
            "You are a subagent executing a specific background task. Execute the task directly " +
            "using your tools — do not design frameworks, save skills, or plan methodology. " +
            "Start calling the required tools immediately. " +
            "Call ReportProgress after each significant step so the user stays informed. " +
            $"For large outputs (reports, document lists, structured data): use save_to_working_memory " +
            $"to store them (set ttl_minutes to 240 or more). Your outputs are stored under namespace " +
            $"'{subagentNamespace}' and the primary agent can retrieve them using " +
            $"list_working_memory(namespace: '{subagentNamespace}') or " +
            $"get_from_working_memory('{subagentNamespace}/your-key'). " +
            "Your final message must summarise what was done and list each key you saved " +
            "so the primary agent knows where to find the detailed data. " +
            "Do not return an empty or vague final response.";

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

        // Working memory tools scoped to this subagent's namespace
        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, subagentNamespace, logger);

        // Registry tools — include MCP data tools and web/script tools.
        // Excluded:
        //   "subagent"           — no spawning nested subagents
        //   "scheduling"         — no creating new scheduled tasks
        //   "a2a"                — invoke_agent is async; results fold into the primary
        //                          session, not the subagent's; silently useless here
        //   mcp_register_server / mcp_unregister_server — infrastructure-only; subagents
        //                          must not reconfigure the MCP bridge
        // Allowed from source "mcp:management":
        //   mcp_invoke_tool, mcp_list_services, mcp_get_service_details — subagents need
        //   these to call MCP servers (calendar, email, openrouter, etc.)
        var registryTools = toolRegistry.GetTools()
            .Where(r => r.Source != "subagent"
                     && r.Source != "scheduling"
                     && r.Source != "a2a"
                     && r.Name != "mcp_register_server"
                     && r.Name != "mcp_unregister_server")
            .Select(r => (AIFunction)new SubagentRegistryToolFunction(
                r, toolRegistry.GetExecutor(r.Name)!, subagentSessionId))
            .ToArray();

        // report_progress tool — baked with taskId and primarySessionId
        var subagentId = $"subagent-{taskId}";
        var reportProgressFunctions = new ReportProgressFunctions(
            taskId, primarySessionId, publisher, subagentId, logger);

        var chatOptions = new ChatOptions
        {
            Tools = [
                ..memoryTools.Tools,
                ..sessionWorkingMemoryTools.Tools,
                ..skillTools.Tools,
                ..toolGuideTools.Tools,
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
        catch (OperationCanceledException oce)
        {
            // Timeout or explicit cancellation — always notify the primary agent
            // so it isn't left waiting indefinitely.
            var reason = ct.IsCancellationRequested ? "cancelled" : "timed out";
            logger.LogWarning("Subagent {TaskId} {Reason}", taskId, reason);
            finalOutput = $"Subagent task was {reason} before completing.";
            isSuccess = false;
            error = oce.Message;
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

        var envelope = result.ToEnvelope<SubagentResultMessage>(source: subagentId);
        await publisher.PublishAsync(SubagentTopics.Result, envelope, CancellationToken.None);

        logger.LogInformation("Subagent {TaskId} published result (success={Success})", taskId, isSuccess);
    }
}
