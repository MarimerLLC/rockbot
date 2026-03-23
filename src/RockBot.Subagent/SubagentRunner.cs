using System.Diagnostics;
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
/// Executes a single subagent task: builds focused context, runs the LLM tool loop,
/// publishes progress via report_progress tool, and publishes final result.
/// Resolved per-task from a DI scope.
/// </summary>
internal sealed class SubagentRunner(
    AgentLoopRunner agentLoopRunner,
    AgentContextBuilder agentContextBuilder,
    ILlmClient llmClient,
    ILlmTierSelector tierSelector,
    IWorkingMemory workingMemory,
    MemoryTools memoryTools,
    ISkillStore skillStore,
    IToolRegistry toolRegistry,
    ToolGuideTools toolGuideTools,
    IMessagePublisher publisher,
    TierRoutingLogger tierRoutingLogger,
    AgentProfile agentProfile,
    ILogger<SubagentRunner> logger)
{
    public async Task RunAsync(
        string taskId,
        string subagentSessionId,
        string description,
        string? context,
        string primarySessionId,
        string? batchId,
        bool consolidate,
        CancellationToken ct)
    {
        var classification = tierSelector.Classify(description);
        var tier = classification.Tier;
        logger.LogInformation(
            "Subagent {TaskId} starting (session {SessionId}) tier={Tier} (score={Score:F3})",
            taskId, subagentSessionId, tier, classification.ComplexityScore);

        var subagentNamespace = $"subagent/{taskId}";

        // Dynamic preamble — includes runtime values (namespace, working memory keys).
        var preamble =
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

        // Build subagent system prompt from profile documents:
        //   preamble → soul → common-directives → subagent-directives → memory-rules → style
        // Common directives carry shared behavioral rules (search, resolve references,
        // execute don't narrate, etc.). Subagent directives add subagent-specific rules
        // (JSON strictness, timezone injection format). Falls back to primary directives
        // if no subagent-specific file exists.
        var profileDocs = new[] {
            agentProfile.Soul,
            agentProfile.CommonDirectives,
            agentProfile.SubagentDirectives ?? agentProfile.Directives,
            agentProfile.MemoryRules,
            agentProfile.Style,
        }.Where(d => d is not null).Select(d => d!.RawContent.TrimEnd());
        var systemPrompt = preamble + "\n\n" + string.Join("\n\n", profileDocs);

        // Use AgentContextBuilder for the full context: system prompt, datetime, rules,
        // model guardrails, long-term memory recall, skill/service hints, working memory.
        // The subagent session has no conversation history, so that section is a no-op.
        var chatMessages = await agentContextBuilder.BuildAsync(
            subagentSessionId, description, ct,
            workingMemoryNamespace: subagentNamespace,
            systemPromptOverride: systemPrompt);

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
                r, toolRegistry.GetExecutor(r.Name)!, subagentNamespace))
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

        // Estimate post-injection context size for telemetry
        var postInjectionTokenEstimate = chatMessages.Sum(m => (m.Text?.Length ?? 0) / 4 + 1);

        string finalOutput;
        bool isSuccess;
        string? error = null;
        var subagentSw = System.Diagnostics.Stopwatch.StartNew();

        using var subagentActivity = SubagentDiagnostics.Source.StartActivity("rockbot.subagent.task");
        subagentActivity?.SetTag("rockbot.subagent.task_id", taskId);
        subagentActivity?.SetTag("rockbot.subagent.primary_session", primarySessionId);
        subagentActivity?.SetTag("rockbot.llm.tier", tier.ToString());
        subagentActivity?.SetTag("rockbot.subagent.description",
            description.Length > 120 ? description[..120] : description);

        try
        {
            using var progressCtx = ToolProgressNotifier.SetContext(new ToolProgressContext
            {
                SessionId = primarySessionId,
                AgentName = $"subagent-{taskId}",
                ReplyTo = UserProxy.UserProxyTopics.UserResponse
            });

            finalOutput = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, subagentSessionId,
                tier: tier, enableFollowUp: false, enableCompletionEval: false,
                cancellationToken: ct);
            finalOutput = ResponseSanitizer.StripTrailingOffers(finalOutput);
            isSuccess = true;
            subagentActivity?.SetStatus(ActivityStatusCode.Ok);
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
            subagentActivity?.SetStatus(ActivityStatusCode.Error, reason);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subagent {TaskId} tool loop failed", taskId);
            finalOutput = $"Task failed: {ex.Message}";
            isSuccess = false;
            error = ex.Message;
            subagentActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }

        subagentSw.Stop();

        SubagentDiagnostics.Duration.Record(subagentSw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("rockbot.subagent.status", isSuccess ? "ok" : "error"));
        if (!isSuccess)
            SubagentDiagnostics.Failures.Add(1);

        _ = tierRoutingLogger.AppendAsync(new TierRoutingEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            PromptPreview = description.Length > 150 ? description[..150] : description,
            Tier = classification.Tier,
            Context = "subagent",
            ComplexityScore = classification.ComplexityScore,
            MatchedHighKeywords = classification.MatchedHighKeywords,
            MatchedLowKeywords = classification.MatchedLowKeywords,
            PostInjectionTokenEstimate = postInjectionTokenEstimate,
            LatencyMs = subagentSw.ElapsedMilliseconds,
        });

        // Publish result
        var result = new SubagentResultMessage
        {
            TaskId = taskId,
            SubagentSessionId = subagentSessionId,
            PrimarySessionId = primarySessionId,
            Output = finalOutput,
            IsSuccess = isSuccess,
            Error = error,
            Timestamp = DateTimeOffset.UtcNow,
            BatchId = batchId,
            Consolidate = consolidate
        };

        var envelope = result.ToEnvelope<SubagentResultMessage>(source: subagentId);
        await publisher.PublishAsync(SubagentTopics.Result, envelope, CancellationToken.None);

        logger.LogInformation("Subagent {TaskId} published result (success={Success})", taskId, isSuccess);
    }
}
