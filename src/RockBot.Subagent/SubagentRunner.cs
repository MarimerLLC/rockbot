using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Tools;

namespace RockBot.Subagent;

/// <summary>
/// Executes a single subagent task: builds focused context, runs the LLM tool loop,
/// publishes progress via report_progress tool, and publishes final result.
/// Resolved per-task from a DI scope.
/// </summary>
internal sealed class SubagentRunner(
    AgentLoopRunner agentLoopRunner,
    IWorkingMemory workingMemory,
    IToolRegistry toolRegistry,
    IMessagePublisher publisher,
    AgentIdentity agentIdentity,
    IWhiteboardMemory whiteboard,
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
            "Produce your final answer as the last message.";

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt)
        };

        if (!string.IsNullOrEmpty(context))
            chatMessages.Add(new ChatMessage(ChatRole.System, $"Context: {context}"));

        chatMessages.Add(new ChatMessage(ChatRole.User, description));

        // Working memory tools scoped to the subagent's session
        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, subagentSessionId, logger);

        // Registry tools (MCP, REST, scheduling, etc.)
        var registryTools = toolRegistry.GetTools()
            .Select(r => (AIFunction)new SubagentRegistryToolFunction(
                r, toolRegistry.GetExecutor(r.Name)!, subagentSessionId))
            .ToArray();

        // report_progress tool — baked with taskId and primarySessionId
        var reportProgressFunctions = new ReportProgressFunctions(
            taskId, primarySessionId, publisher, agentIdentity, logger);

        // Whiteboard tools — use taskId as boardId for namespacing
        var whiteboardFunctions = new WhiteboardFunctions(whiteboard, taskId, logger);

        var chatOptions = new ChatOptions
        {
            Tools = [
                ..sessionWorkingMemoryTools.Tools,
                ..registryTools,
                ..reportProgressFunctions.Tools,
                ..whiteboardFunctions.Tools
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
