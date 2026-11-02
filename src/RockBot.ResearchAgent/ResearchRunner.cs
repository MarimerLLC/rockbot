using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Memory;
using RockBot.Tools;

namespace RockBot.ResearchAgent;

/// <summary>
/// Runs one research question: web search and page reads through <see cref="AgentLoopRunner"/>
/// on the High tier, then a fallback synthesis from working memory if the loop ran out of
/// iterations first. Shared by the MCP <c>research</c> tool and the A2A receiver.
/// </summary>
/// <remarks>
/// Each run works in its own working-memory namespace, <c>research/{runId}</c>, and clears it when
/// it finishes: the process is long-lived now, so chunks from finished runs must not pile up.
/// The concurrency cap is applied around it by <see cref="GatedResearchRunner"/>.
/// </remarks>
internal sealed class ResearchRunner(
    AgentLoopRunner agentLoopRunner,
    IChatClient chatClient,
    IToolRegistry toolRegistry,
    IWorkingMemory workingMemory,
    ILogger<ResearchRunner> logger) : IResearchRunner
{
    internal const string SystemPrompt =
        """
        You are ResearchAgent, an on-demand research specialist.
        Your job is to answer the question by searching the web and reading relevant pages.

        Guidelines:
        - Use web_search to find relevant sources, then web_browse to read them.
        - Retrieve and read at least 2–3 sources before synthesising your answer.
        - If a page is large and chunked into working memory, retrieve the index chunk (key ending
          in -index) first for a hierarchical outline of sections, then load only the relevant chunks.
        - Write a well-structured, factual answer. Cite sources where helpful.
        - Be concise but complete. Any clarifying question was settled before research started:
          do not ask the user anything now. If the request names an interpretation, research that
          one; otherwise answer with the best information available.
        """;

    public async Task<ResearchOutcome> RunAsync(
        ResearchRequest request,
        string runId,
        Func<string, CancellationToken, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        var workingMemoryNamespace = $"research/{runId}";
        var sources = new SourceLog();
        try
        {
            // Pass the full namespace as SessionId so WebBrowseToolExecutor stores chunks under
            // "research/{runId}/web-...".
            var webTools = toolRegistry.GetTools()
                .Select(r => (AIFunction)new ResearchToolFunction(
                    r, toolRegistry.GetExecutor(r.Name)!, workingMemoryNamespace, sources))
                .ToArray();

            var workingMemoryTools = new WorkingMemoryTools(workingMemory, workingMemoryNamespace, logger);

            var chatOptions = new ChatOptions
            {
                Tools = [.. webTools, .. workingMemoryTools.Tools]
            };

            var userMessage = BuildUserMessage(request);
            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, userMessage)
            };

            var finalContent = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, workingMemoryNamespace, tier: ModelTier.High,
                onProgress: onProgress, enableFollowUp: false, cancellationToken: cancellationToken,
                actionGate: ActionGateScope.ForA2AInbound);

            // If the loop exhausted iterations before synthesising (returns empty or very short),
            // the model likely saved findings to working memory. Read them back and synthesise
            // directly so the caller always gets a usable answer.
            if (finalContent.Length < 100)
            {
                logger.LogWarning(
                    "Research run {RunId} loop returned {Len} chars — attempting fallback synthesis from working memory",
                    runId, finalContent.Length);

                finalContent = await SynthesiseFromWorkingMemoryAsync(workingMemoryNamespace, userMessage, cancellationToken);
            }

            logger.LogInformation("Research run {RunId} completed, output length={Len}, sources={Sources}",
                runId, finalContent.Length, sources.Sources.Count);

            return new ResearchOutcome(finalContent, sources.Sources);
        }
        finally
        {
            try
            {
                await workingMemory.ClearAsync(workingMemoryNamespace + "/");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not clear working memory for research run {RunId}", runId);
            }
        }
    }

    /// <summary>The question as the research loop sees it, with its context, constraints and interpretation.</summary>
    internal static string BuildUserMessage(ResearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Context)
            && string.IsNullOrWhiteSpace(request.Constraints)
            && string.IsNullOrWhiteSpace(request.Interpretation))
            return request.Question;

        var sb = new StringBuilder();
        sb.AppendLine($"Question: {request.Question}");
        if (!string.IsNullOrWhiteSpace(request.Interpretation))
            sb.AppendLine($"Research this interpretation of the question: {request.Interpretation}");
        if (!string.IsNullOrWhiteSpace(request.Context))
            sb.AppendLine($"Context from the person asking: {request.Context}");
        if (!string.IsNullOrWhiteSpace(request.Constraints))
            sb.AppendLine($"Constraints: {request.Constraints}");
        return sb.ToString().TrimEnd();
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
