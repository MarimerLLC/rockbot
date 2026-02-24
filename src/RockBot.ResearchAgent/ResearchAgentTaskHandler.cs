using System.Text;
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
    IChatClient chatClient,
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

            // Working memory namespace: "research/{taskId}" — distinct from user sessions
            // and surfaced in the primary agent's context if the primary agent browses "research/"
            var workingMemoryNamespace = $"research/{sessionId}";

            // Build web tool AIFunctions — pass the full namespace as SessionId so
            // WebBrowseToolExecutor stores chunks under "research/{taskId}/web-..."
            var webTools = toolRegistry.GetTools()
                .Select(r => (AIFunction)new ResearchToolFunction(
                    r, toolRegistry.GetExecutor(r.Name)!, workingMemoryNamespace))
                .ToArray();

            // Working memory tools let the LLM retrieve chunked page content
            var workingMemoryTools = new WorkingMemoryTools(workingMemory, workingMemoryNamespace, logger);

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
                chatMessages, chatOptions, sessionId, tier: ModelTier.High,
                onProgress: onProgress, cancellationToken: ct);

            // If the loop exhausted iterations before synthesising (returns empty or very short),
            // the model likely saved findings to working memory. Read them back and synthesise
            // directly so the caller always gets a usable answer.
            if (finalContent.Length < 100)
            {
                logger.LogWarning(
                    "Research task {TaskId} loop returned {Len} chars — attempting fallback synthesis from working memory",
                    request.TaskId, finalContent.Length);

                finalContent = await SynthesiseFromWorkingMemoryAsync(workingMemoryNamespace, question, ct);
            }

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
            shutdown.NotifyTaskComplete();
        }
    }

    /// <summary>
    /// Fallback: reads all working memory entries saved during the research loop and
    /// calls the LLM once (no tools) to synthesise a final answer from them.
    /// Used when the tool loop exhausts its iteration limit before producing output.
    /// </summary>
    private async Task<string> SynthesiseFromWorkingMemoryAsync(
        string @namespace, string question, CancellationToken ct)
    {
        var entries = await workingMemory.ListAsync(@namespace);
        if (entries.Count == 0)
        {
            logger.LogWarning("Fallback synthesis: no working memory entries found for namespace {Namespace}", @namespace);
            return "Research completed but the synthesis step was unable to produce a result. Please try again.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("You were researching a question but ran out of tool-calling iterations before writing your final answer.");
        sb.AppendLine("Below are the research findings you saved to working memory during the research loop.");
        sb.AppendLine("Synthesise a comprehensive, well-structured answer to the original question from these findings.");
        sb.AppendLine();
        sb.AppendLine($"Original question: {question}");
        sb.AppendLine();
        sb.AppendLine("--- Saved Research Findings ---");

        foreach (var entry in entries)
        {
            var value = await workingMemory.GetAsync(entry.Key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                sb.AppendLine();
                sb.AppendLine($"### {entry.Key}");
                sb.AppendLine(value);
            }
        }

        logger.LogInformation(
            "Fallback synthesis: collected {Count} working memory entries ({Len} chars total) for namespace {Namespace}",
            entries.Count, sb.Length, @namespace);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, sb.ToString())
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
        var synthesised = response.Text?.Trim() ?? string.Empty;

        logger.LogInformation(
            "Fallback synthesis produced {Len} chars for namespace {Namespace}", synthesised.Length, @namespace);

        return string.IsNullOrWhiteSpace(synthesised)
            ? "Research completed but synthesis produced no output. Please try again."
            : synthesised;
    }

}
