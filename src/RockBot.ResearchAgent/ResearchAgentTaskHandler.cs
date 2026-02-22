using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Memory;
using RockBot.Tools;

namespace RockBot.ResearchAgent;

/// <summary>
/// Handles incoming <see cref="AgentTaskRequest"/> messages by running a multi-step
/// web search / page fetch / LLM synthesis loop via <see cref="AgentLoopRunner"/>.
/// On completion (success or failure) the ephemeral pod is signalled to shut down.
/// </summary>
internal sealed class ResearchAgentTaskHandler(
    AgentLoopRunner agentLoopRunner,
    IToolRegistry toolRegistry,
    IWorkingMemory workingMemory,
    EphemeralShutdownCoordinator shutdown,
    ILogger<ResearchAgentTaskHandler> logger) : IAgentTaskHandler
{
    private const string SystemPrompt =
        """
        You are ResearchAgent, an on-demand research specialist.
        Your job is to answer the user's question by searching the web and reading relevant pages.

        Guidelines:
        - Use web_search to find relevant sources, then web_browse to read them.
        - Retrieve and read at least 2–3 sources before synthesising your answer.
        - If a page is large and chunked into working memory, call get_from_working_memory for each chunk.
        - Write a well-structured, factual answer. Cite sources where helpful.
        - Be concise but complete. Do not ask clarifying questions — answer with the best information available.
        """;

    public async Task<AgentTaskResult> HandleTaskAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;
        var sessionId = request.TaskId;

        logger.LogInformation("Handling research task {TaskId} (skill={Skill})", request.TaskId, request.Skill);

        try
        {
            // Notify caller we're actively working
            await context.PublishStatus(new AgentTaskStatusUpdate
            {
                TaskId = request.TaskId,
                ContextId = request.ContextId,
                State = AgentTaskState.Working
            }, ct);

            var question = request.Message.Parts
                .Where(p => p.Kind == "text")
                .Select(p => p.Text)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                ?? "(no question provided)";

            logger.LogInformation("Research question for task {TaskId}: {Question}", request.TaskId, question);

            // Build web tool AIFunctions from the tool registry
            var webTools = toolRegistry.GetTools()
                .Select(r => (AIFunction)new ResearchToolFunction(
                    r, toolRegistry.GetExecutor(r.Name)!, sessionId))
                .ToArray();

            // Working memory tools let the LLM retrieve chunked page content
            var workingMemoryTools = new WorkingMemoryTools(workingMemory, sessionId, logger);

            var chatOptions = new ChatOptions
            {
                Tools = [..webTools, ..workingMemoryTools.Tools]
            };

            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, question)
            };

            var onProgress = async (string progressMsg, CancellationToken progressCt) =>
            {
                await context.PublishStatus(new AgentTaskStatusUpdate
                {
                    TaskId = request.TaskId,
                    ContextId = request.ContextId,
                    State = AgentTaskState.Working,
                    Message = new AgentMessage
                    {
                        Role = "agent",
                        Parts = [new AgentMessagePart { Kind = "text", Text = progressMsg }]
                    }
                }, progressCt);
            };

            var finalContent = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, sessionId, onProgress: onProgress, cancellationToken: ct);

            logger.LogInformation("Research task {TaskId} completed, output length={Len}",
                request.TaskId, finalContent.Length);

            return new AgentTaskResult
            {
                TaskId = request.TaskId,
                ContextId = request.ContextId,
                State = AgentTaskState.Completed,
                Message = new AgentMessage
                {
                    Role = "agent",
                    Parts = [new AgentMessagePart { Kind = "text", Text = finalContent }]
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Research task {TaskId} failed", request.TaskId);
            throw;
        }
        finally
        {
            // Always signal shutdown — whether success or failure.
            // The framework (AgentTaskRequestHandler) has already received the result/exception
            // and will publish the response/error before graceful shutdown completes.
            shutdown.NotifyTaskComplete();
        }
    }
}
