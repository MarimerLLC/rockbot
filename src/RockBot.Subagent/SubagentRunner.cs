using System.Diagnostics;
using System.Text.Json;
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
    TieredChatClientRegistry registry,
    IWorkingMemory workingMemory,
    MemoryTools memoryTools,
    ISkillStore skillStore,
    IToolRegistry toolRegistry,
    ToolGuideTools toolGuideTools,
    IMessagePublisher publisher,
    AgentIdentity agent,
    TierRoutingLogger tierRoutingLogger,
    AgentProfile agentProfile,
    SessionClientCapabilityStore clientCapabilityStore,
    ILogger<SubagentRunner> logger,
    ISkillResourceUsageStore? skillResourceUsageStore = null,
    ISessionA2AAwaiter? a2aAwaiter = null)
{
    public async Task RunAsync(
        string taskId,
        string subagentSessionId,
        string description,
        string? context,
        string primarySessionId,
        string? batchId,
        bool consolidate,
        int? maxIterations,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var classification = tierSelector.Classify(description, new TierRoutingContext(Origin: "subagent"));
        var tier = classification.Tier;
        logger.LogInformation(
            "Subagent {TaskId} starting (session {SessionId}) tier={Tier} (score={Score:F3})",
            taskId, subagentSessionId, tier, classification.ComplexityScore);

        var subagentNamespace = $"subagent/{taskId}";
        var whiteboardCategory = $"subagent-whiteboards/{taskId}";

        // Dynamic preamble — includes runtime values (namespace, working memory keys).
        var preamble =
            "You are a subagent executing a specific background task. Execute the task directly " +
            "using your tools — do not design frameworks, save skills, or plan methodology. " +
            "Start calling the required tools immediately. " +
            "Call ReportProgress after each significant step so the user stays informed. " +
            "For repetitive operations with known parameters (e.g., the same API call across " +
            "multiple date ranges, accounts, or categories), use spawn_wisps to execute them " +
            "in parallel rather than calling tools sequentially. Wisps don't consume your " +
            "iteration budget and run concurrently. Use Direct mode steps with the correct " +
            "gateway and put tool arguments in the \"params\" field. " +
            $"For large outputs (reports, document lists, structured data): use save_to_working_memory " +
            $"to store them (set ttl_minutes to 240 or more). Your working memory namespace is " +
            $"'{subagentNamespace}' and the primary agent can retrieve them using " +
            $"search_working_memory(namespace: '{subagentNamespace}') or " +
            $"get_from_working_memory('{subagentNamespace}/your-key'). " +
            $"For long-term memory entries the primary agent should read after you complete, use " +
            $"the category '{whiteboardCategory}' when calling save_memory. This is a long-term " +
            $"memory CATEGORY (passed to save_memory's category parameter), NOT a working-memory " +
            $"key — do not use it as a key with save_to_working_memory. Substitute the literal " +
            $"value above; never write the placeholder text {{task_id}} into a category or key. " +
            "Your final message must summarise what was done and list each key you saved " +
            "so the primary agent knows where to find the detailed data. " +
            "Do not return an empty or vague final response.";

        // Build subagent system prompt from profile documents:
        //   preamble → soul → safety-rules → common-directives → subagent-directives → memory-rules → style
        // Safety rules are the shared prompt-injection guardrail (also injected to workers).
        // Common directives carry cross-rung behavioral rules (search, resolve references, etc.).
        // Subagent directives add subagent-specific rules (whiteboard category, spawn scope,
        // pattern-review walk). Falls back to primary directives if no subagent-specific file exists.
        var profileDocs = new[] {
            agentProfile.Soul,
            agentProfile.SafetyRules,
            agentProfile.CommonDirectives,
            agentProfile.SubagentDirectives ?? agentProfile.Directives,
            agentProfile.MemoryRules,
            agentProfile.Style,
        }.Where(d => d is not null).Select(d => d!.RawContent.TrimEnd());
        var systemPrompt = preamble + "\n\n" + string.Join("\n\n", profileDocs);

        // Use AgentContextBuilder for the full context: system prompt, datetime, rules,
        // model guardrails, long-term memory recall, skill/service hints, working memory.
        // The subagent session has no conversation history, so that section is a no-op.
        //
        // primarySessionId is the WM namespace (e.g. "session/blazor-session") for user
        // sessions; the capability stash is keyed by raw session id (e.g. "blazor-session").
        // Strip the "session/" prefix to match — for non-user origins (subagent/wisp) the
        // lookup naturally misses and returns None.
        const string SessionPrefix = "session/";
        var primaryRawSessionId = primarySessionId.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
            ? primarySessionId[SessionPrefix.Length..]
            : primarySessionId;
        var chatMessages = await agentContextBuilder.BuildAsync(
            subagentSessionId, description, ct,
            workingMemoryNamespace: subagentNamespace,
            systemPromptOverride: systemPrompt,
            clientCapabilities: clientCapabilityStore.Get(primaryRawSessionId));

        if (!string.IsNullOrEmpty(context))
            chatMessages.Add(new ChatMessage(ChatRole.System, $"Context: {context}"));

        chatMessages.Add(new ChatMessage(ChatRole.User, description));

        // Long-term memory tools (search_memory, save_memory, etc.)
        // MemoryTools is a singleton — safe to use directly.

        // Skill tools (get_skill, list_skills, save_skill, promote_skill_asset). Subagents
        // are the only path that gets promote_skill_asset — they perform the exploratory
        // tool-call discovery whose result is worth capturing as a typed asset; the main
        // agent reaches assets via skills the dream pass has already promoted.
        var skillTools = new SkillTools(skillStore, llmClient, logger, subagentSessionId,
            enablePromote: true, resourceUsageStore: skillResourceUsageStore);

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
        // ToolProfiles.Subagent encodes the deny rules described in the comment above
        // (no nested subagents, no scheduling, no async A2A, no MCP bridge reconfig).
        // The inline .Select with SubagentRegistryToolFunction is preserved — only the
        // predicate moves to the shared profile so the surface is drift-tested.
        var registryTools = toolRegistry.GetTools()
            .Where(ToolProfiles.Subagent.Matches)
            .Select(r => (AIFunction)new SubagentRegistryToolFunction(
                r, toolRegistry.GetExecutor(r.Name)!, subagentNamespace))
            .ToArray();

        // report_progress tool — baked with taskId and primarySessionId.
        // The onReport callback feeds a rolling buffer used to populate the
        // failure-details working-memory entry when the subagent fails or is cancelled.
        var subagentId = $"subagent-{taskId}";
        var recentProgress = new System.Collections.Generic.Queue<(DateTimeOffset At, string Message)>();
        const int RecentProgressCapacity = 5;
        var reportProgressFunctions = new ReportProgressFunctions(
            taskId, primarySessionId, publisher, subagentId, agent.Name, logger,
            onReport: msg =>
            {
                lock (recentProgress)
                {
                    recentProgress.Enqueue((DateTimeOffset.UtcNow, msg));
                    while (recentProgress.Count > RecentProgressCapacity)
                        recentProgress.Dequeue();
                }
            });

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
        string? failureReason = null;
        var diagnostics = new LoopDiagnostics();
        var subagentSw = System.Diagnostics.Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;

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
                ReplyTo = $"{UserProxy.UserProxyTopics.UserResponse}.{agent.Name}"
            });

            finalOutput = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, subagentSessionId,
                tier: tier, enableFollowUp: false, enableCompletionEval: false,
                maxIterationsOverride: maxIterations,
                diagnostics: diagnostics,
                cancellationToken: ct);
            finalOutput = ResponseSanitizer.StripTrailingOffers(finalOutput);
            isSuccess = true;
            subagentActivity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException)
        {
            // Distinguish timeout (CTS fired at deadline) from explicit cancel
            // (manager called CancelAsync before the deadline). We compare elapsed
            // time against the configured timeout — if we're at or past the
            // deadline the trigger was the timer, otherwise it was an external call.
            var elapsedAtCancel = DateTimeOffset.UtcNow - startedAt;
            failureReason = timeout > TimeSpan.Zero && elapsedAtCancel >= timeout - TimeSpan.FromSeconds(2)
                ? "timeout"
                : "cancelled";
            logger.LogWarning(
                "Subagent {TaskId} {Reason} after {ElapsedSec:F1}s (timeout={TimeoutSec:F0}s)",
                taskId, failureReason, elapsedAtCancel.TotalSeconds, timeout.TotalSeconds);
            finalOutput = failureReason == "timeout"
                ? $"Subagent timed out after {timeout.TotalMinutes:0.#} minutes. " +
                  $"Failure diagnostics in working memory at 'subagent/{taskId}/failure-details'."
                : $"Subagent was cancelled before completing. " +
                  $"Failure diagnostics in working memory at 'subagent/{taskId}/failure-details'.";
            isSuccess = false;
            error = failureReason == "timeout"
                ? $"Timed out after {timeout.TotalMinutes:0.#} minutes"
                : "Cancelled by request";
            subagentActivity?.SetStatus(ActivityStatusCode.Error, failureReason);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subagent {TaskId} tool loop failed", taskId);
            failureReason = "exception";
            finalOutput = $"Subagent failed: {ex.Message}. " +
                          $"Failure diagnostics in working memory at 'subagent/{taskId}/failure-details'.";
            isSuccess = false;
            error = ex.Message;
            subagentActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }

        // On any non-success path, write a structured failure-details entry to working
        // memory so the primary agent can answer "why did it fail" without having to
        // re-investigate from logs. Lives under the same subagent/{taskId}/ namespace
        // as the rest of the subagent's outputs so the SubagentResultHandler's existing
        // whiteboard-hint flow surfaces it automatically.
        if (!isSuccess)
        {
            await SaveFailureDetailsAsync(
                taskId, description, failureReason ?? "unknown",
                error, startedAt, subagentSw.Elapsed, timeout,
                diagnostics, recentProgress, ct);
        }

        // Wait for any in-flight A2A tasks the subagent dispatched (via wisps) to
        // reach a terminal state before publishing the subagent result. Without this,
        // the wisp's "dispatched, result arrives async" semantics let the subagent's
        // LLM declare success while the target agent is still working — and the late
        // A2A response is then dropped by A2ATaskResultHandler because its originating
        // subagent session has exited and is not a user session. The wait uses the
        // subagent's overall CancellationToken so a subagent timeout/cancel doesn't
        // get stretched waiting on remote agents. Once the wait completes, A2A result
        // working-memory keys (subagent/{taskId}/a2a/.../result) are populated and
        // SubagentResultHandler's existing whiteboard listing surfaces them to the
        // primary agent's synthesis turn automatically.
        if (a2aAwaiter is not null)
        {
            try
            {
                var awaited = await a2aAwaiter.WaitForSessionAsync(subagentNamespace, ct);
                if (awaited > 0)
                {
                    logger.LogInformation(
                        "Subagent {TaskId} awaited {Count} A2A task(s) before publishing result",
                        taskId, awaited);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Subagent {TaskId} A2A await threw — continuing to publish anyway", taskId);
            }
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
            ModelId = diagnostics.ModelId ?? registry.GetModelId(classification.Tier),
            InputTokens = diagnostics.InputTokens > 0 ? diagnostics.InputTokens : null,
            OutputTokens = diagnostics.OutputTokens > 0 ? diagnostics.OutputTokens : null,
            ToolCallCount = diagnostics.ToolCalls > 0 ? diagnostics.ToolCalls : null,
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
        await publisher.PublishAsync($"{SubagentTopics.Result}.{agent.Name}", envelope, CancellationToken.None);

        logger.LogInformation("Subagent {TaskId} published result (success={Success})", taskId, isSuccess);
    }

    /// <summary>
    /// Persists a structured failure-details payload to working memory under
    /// <c>subagent/{taskId}/failure-details</c>. The primary agent reads this entry
    /// (surfaced automatically by SubagentResultHandler's whiteboard hint) to answer
    /// "why did the subagent fail" without having to dig through pod logs.
    /// </summary>
    private async Task SaveFailureDetailsAsync(
        string taskId,
        string description,
        string reason,
        string? errorMessage,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        TimeSpan timeout,
        LoopDiagnostics diagnostics,
        Queue<(DateTimeOffset At, string Message)> recentProgress,
        CancellationToken ct)
    {
        try
        {
            (DateTimeOffset At, string Message)[] progressSnapshot;
            lock (recentProgress)
                progressSnapshot = recentProgress.ToArray();

            var json = BuildFailureDetailsPayload(
                taskId, description, reason, errorMessage,
                startedAt, elapsed, timeout, diagnostics, progressSnapshot);

            // TTL 240 minutes matches the convention subagents use for their other
            // long-lived outputs so the primary can refer back across follow-ups.
            await workingMemory.SetAsync(
                $"subagent/{taskId}/failure-details",
                json,
                TimeSpan.FromMinutes(240),
                category: "subagent-failure",
                tags: ["subagent-failure", reason]);

            logger.LogInformation(
                "Subagent {TaskId} failure details saved to working memory (reason={Reason}, lastTool={Tool})",
                taskId, reason, diagnostics.LastToolName ?? "(none)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Failure-detail persistence is best-effort — never block the result publish.
            logger.LogWarning(ex,
                "Failed to persist failure details for subagent {TaskId}; primary agent will see only the inline reason",
                taskId);
        }
    }

    /// <summary>
    /// Builds the JSON payload that <see cref="SaveFailureDetailsAsync"/> writes to
    /// working memory. Exposed as <c>internal static</c> so unit tests can exercise
    /// the payload shape without instantiating the full SubagentRunner DI graph.
    /// </summary>
    internal static string BuildFailureDetailsPayload(
        string taskId,
        string description,
        string reason,
        string? errorMessage,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        TimeSpan timeout,
        LoopDiagnostics diagnostics,
        IReadOnlyList<(DateTimeOffset At, string Message)> recentProgress)
    {
        var payload = new
        {
            taskId,
            reason,
            description = description.Length > 500 ? description[..500] + "…" : description,
            error = errorMessage,
            startedAt = startedAt.ToString("O"),
            elapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            timeoutMinutes = timeout.TotalMinutes > 0 ? Math.Round(timeout.TotalMinutes, 1) : (double?)null,
            iterations = diagnostics.Iterations,
            toolCalls = diagnostics.ToolCalls,
            lastAssistantText = diagnostics.LastAssistantText,
            lastTool = diagnostics.LastToolName is null ? null : new
            {
                name = diagnostics.LastToolName,
                arguments = diagnostics.LastToolArguments,
                status = diagnostics.LastToolStatus,
                result = diagnostics.LastToolResult,
                startedAt = diagnostics.LastToolStartedAt?.ToString("O"),
                completedAt = diagnostics.LastToolCompletedAt?.ToString("O"),
            },
            recentProgress = recentProgress.Select(p => new
            {
                at = p.At.ToString("O"),
                message = p.Message
            }).ToArray(),
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }
}
