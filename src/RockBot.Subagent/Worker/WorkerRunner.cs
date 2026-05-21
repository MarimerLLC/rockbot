using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Tools;

namespace RockBot.Subagent.Worker;

/// <summary>
/// Per-task runner abstraction. Extracted as an interface so
/// <see cref="WorkerManager"/> can be unit-tested with a stub runner without
/// resolving the full LLM/context-builder DI graph.
/// </summary>
public interface IWorkerRunner
{
    Task<WorkerResult> RunAsync(
        string taskId,
        WorkerDefinition definition,
        string primarySessionId,
        string batchId,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>
/// Executes a single worker task: builds slim context, runs the lean LLM loop,
/// and writes a structured <see cref="WorkerResult"/> to the result key. Resolved
/// per-task from a DI scope so per-task tool wiring (working memory namespace,
/// report-progress baking) stays self-contained.
/// </summary>
internal sealed class WorkerRunner(
    AgentLoopRunner agentLoopRunner,
    AgentContextBuilder agentContextBuilder,
    IWorkingMemory workingMemory,
    IToolRegistry toolRegistry,
    IOptions<WorkerOptions> options,
    IMessagePublisher publisher,
    AgentIdentity agent,
    AgentProfile agentProfile,
    ILogger<WorkerRunner> logger) : IWorkerRunner
{
    /// <summary>
    /// Tool sources that workers never see. Source matches drop the tool from the
    /// registry-derived AIFunction set before allowlist filtering.
    /// </summary>
    private static readonly HashSet<string> ExcludedSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "subagent",   // workers are leaf nodes
        "worker",     // no nested spawn_workers
        "scheduling", // no creating scheduled tasks
        "a2a",        // invoke_agent results fold into a different session
    };

    /// <summary>
    /// Specific tool names workers never see, regardless of source. These are
    /// curation/admin tools that the lean LLM lacks the context to use well.
    /// </summary>
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mcp_register_server",
        "mcp_unregister_server",
        "save_memory",
        "save_skill",
        "promote_skill_asset",
        "update_task_directive",
        "invoke_agent",
    };

    public async Task<WorkerResult> RunAsync(
        string taskId,
        WorkerDefinition definition,
        string primarySessionId,
        string batchId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var workerSessionId = $"worker-{taskId}";
        var workerNamespace = $"worker/{taskId}";
        var resultKey = definition.ResultKey ?? $"worker/{taskId}/result";

        logger.LogInformation(
            "Worker {TaskId} starting (session {SessionId}, batch {BatchId}, result_key={ResultKey})",
            taskId, workerSessionId, batchId, resultKey);

        var systemPrompt = BuildSystemPrompt(taskId, workerNamespace, resultKey);
        var chatMessages = await agentContextBuilder.BuildForWorkerAsync(
            workerSessionId, definition.Description, ct,
            workingMemoryNamespace: workerNamespace,
            systemPromptOverride: systemPrompt);

        if (!string.IsNullOrEmpty(definition.Context))
            chatMessages.Add(new ChatMessage(ChatRole.System, $"Context: {definition.Context}"));

        chatMessages.Add(new ChatMessage(ChatRole.User, definition.Description));

        // Working memory tools scoped to the worker's namespace.
        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, workerNamespace, logger);

        // Registry tools — filter out forbidden sources/names, then narrow by tools_allow.
        var registryTools = BuildRegistryTools(definition.ToolsAllow, workerNamespace);

        var subagentId = $"worker-{taskId}";
        var recentProgress = new Queue<(DateTimeOffset At, string Message)>();
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
                ..sessionWorkingMemoryTools.Tools,
                ..registryTools,
                ..reportProgressFunctions.Tools,
            ],
        };

        string finalOutput = string.Empty;
        bool isSuccess;
        string? failureReason = null;
        var diagnostics = new LoopDiagnostics();
        var workerSw = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        var maxIterations = options.Value.DefaultMaxIterations;

        using var workerActivity = WorkerDiagnostics.Source.StartActivity("rockbot.worker.task");
        workerActivity?.SetTag("rockbot.worker.task_id", taskId);
        workerActivity?.SetTag("rockbot.worker.batch_id", batchId);
        workerActivity?.SetTag("rockbot.worker.primary_session", primarySessionId);
        workerActivity?.SetTag("rockbot.subagent_type", "worker");
        workerActivity?.SetTag("rockbot.worker.description",
            definition.Description.Length > 120 ? definition.Description[..120] : definition.Description);

        try
        {
            using var progressCtx = ToolProgressNotifier.SetContext(new ToolProgressContext
            {
                SessionId = primarySessionId,
                AgentName = subagentId,
                ReplyTo = $"{UserProxy.UserProxyTopics.UserResponse}.{agent.Name}",
            });

            finalOutput = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, workerSessionId,
                tier: ModelTier.Low,
                enableFollowUp: false,
                enableCompletionEval: false,
                enableReasoningScaffolding: false,
                maxIterationsOverride: maxIterations,
                diagnostics: diagnostics,
                cancellationToken: ct);
            isSuccess = true;
            workerActivity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException)
        {
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            failureReason = timeout > TimeSpan.Zero && elapsed >= timeout - TimeSpan.FromSeconds(2)
                ? "timeout"
                : "cancelled";
            isSuccess = false;
            workerActivity?.SetStatus(ActivityStatusCode.Error, failureReason);
            logger.LogWarning(
                "Worker {TaskId} {Reason} after {ElapsedSec:F1}s (timeout={TimeoutSec:F0}s)",
                taskId, failureReason, elapsed.TotalSeconds, timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            failureReason = $"exception: {ex.Message}";
            isSuccess = false;
            workerActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "Worker {TaskId} tool loop failed", taskId);
        }

        workerSw.Stop();
        WorkerDiagnostics.Duration.Record(workerSw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("rockbot.worker.status", isSuccess ? "ok" : "error"),
            new KeyValuePair<string, object?>("rockbot.subagent_type", "worker"));
        if (!isSuccess)
            WorkerDiagnostics.Failures.Add(1,
                new KeyValuePair<string, object?>("rockbot.subagent_type", "worker"));

        var (factsRecorded, blocked, convergedPatterns) = ParseWorkerSelfReport(finalOutput);

        var result = new WorkerResult
        {
            TaskId = taskId,
            IsSuccess = isSuccess,
            ResultKey = resultKey,
            FactsRecorded = factsRecorded,
            Blocked = blocked,
            ConvergedPatterns = convergedPatterns,
            Duration = workerSw.Elapsed,
            LlmTurns = diagnostics.Iterations,
            FailureReason = failureReason,
        };

        // Do NOT write the receipt to resultKey — the worker has already saved its
        // findings there. The receipt is returned to the spawning agent inline via
        // the spawn_workers tool response (see SpawnWorkersExecutor.FormatBatchReceipt).
        // Failures still get a separate failure-details entry below.

        if (!isSuccess)
        {
            await SaveFailureDetailsAsync(
                taskId, definition.Description, failureReason ?? "unknown",
                startedAt, workerSw.Elapsed, timeout, diagnostics, recentProgress, ct);
        }

        logger.LogInformation(
            "Worker {TaskId} complete (success={Success}, llmTurns={Turns}, durationMs={Ms})",
            taskId, isSuccess, diagnostics.Iterations, workerSw.ElapsedMilliseconds);

        return result;
    }

    private string BuildSystemPrompt(string taskId, string workerNamespace, string resultKey)
    {
        // Slim preamble — see design/worker-subagents.md. Substitute runtime values
        // (task id, namespace, result key) so the worker references concrete keys
        // instead of placeholder text.
        var preamble =
            "You execute a focused gather task. You are not the primary agent and you do not deliberate about persona, history, or motivation.\n" +
            $"Your task id: {taskId}\n" +
            $"Your working memory namespace: {workerNamespace}\n" +
            $"Your result key: {resultKey}\n\n" +
            "- Read the task description and any context the spawning agent provided. Treat that context as ground truth.\n" +
            "- Use the available tools (MCP data calls, spawn_wisps for deterministic multi-step sequences, " +
            "working memory) to gather facts.\n" +
            "- Save your structured findings to the result key listed above using save_to_working_memory.\n" +
            "- Call report_progress only when a step takes more than a few seconds or you hit a blocker — " +
            "not for narration.\n" +
            "- When the task is done, return ONE short line ending in a structured marker so the runner " +
            "can extract counts. The marker format is:\n" +
            "  [WORKER_RESULT] facts=<int> blocked=<comma,separated,list,or,empty> patterns=<int>\n" +
            "  Example: \"Saved calendar events for 6 accounts. [WORKER_RESULT] facts=6 blocked= patterns=0\".\n" +
            "  Do not summarise the findings in the reply — the spawning agent reads them from the result key.\n";

        // Add agent profile docs in priority order: soul, common-directives, worker-directives
        // (falling back to subagent-directives if no worker-specific file exists),
        // memory-rules. Skip style — workers don't speak to users.
        var docs = new List<AgentProfileDocument?>
        {
            agentProfile.Soul,
            agentProfile.CommonDirectives,
            agentProfile.WorkerDirectives ?? agentProfile.SubagentDirectives,
            agentProfile.MemoryRules,
        };
        var docTexts = docs.Where(d => d is not null).Select(d => d!.RawContent.TrimEnd());
        return preamble + "\n\n" + string.Join("\n\n", docTexts);
    }

    private List<AIFunction> BuildRegistryTools(IReadOnlyList<string>? toolsAllow, string workerNamespace)
    {
        var allowedRegistrations = toolRegistry.GetTools()
            .Where(r => !ExcludedSources.Contains(r.Source ?? string.Empty))
            .Where(r => !ExcludedNames.Contains(r.Name))
            .Where(r => MatchesAllowlist(r.Name, toolsAllow))
            .ToList();

        var tools = new List<AIFunction>(allowedRegistrations.Count);
        foreach (var reg in allowedRegistrations)
        {
            var executor = toolRegistry.GetExecutor(reg.Name);
            if (executor is null) continue;
            tools.Add(new SubagentRegistryToolFunction(reg, executor, workerNamespace));
        }
        return tools;
    }

    internal static bool MatchesAllowlist(string toolName, IReadOnlyList<string>? toolsAllow)
    {
        if (toolsAllow is null || toolsAllow.Count == 0) return true;

        foreach (var entry in toolsAllow)
        {
            if (string.IsNullOrEmpty(entry)) continue;

            if (entry.EndsWith('*'))
            {
                var prefix = entry[..^1];
                if (prefix.Length == 0) return true;
                if (toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (string.Equals(toolName, entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Parses the final assistant text for a <c>[WORKER_RESULT]</c> marker emitted by
    /// the worker's last turn (see <see cref="BuildSystemPrompt"/>). Falls back to
    /// zero/empty values when the marker is missing or unparseable.
    /// </summary>
    internal static (int FactsRecorded, IReadOnlyList<string> Blocked, IReadOnlyList<ConvergedPattern> ConvergedPatterns)
        ParseWorkerSelfReport(string finalOutput)
    {
        IReadOnlyList<string> blocked = [];
        IReadOnlyList<ConvergedPattern> patterns = [];
        var facts = 0;

        if (string.IsNullOrWhiteSpace(finalOutput))
            return (facts, blocked, patterns);

        var markerIdx = finalOutput.LastIndexOf("[WORKER_RESULT]", StringComparison.OrdinalIgnoreCase);
        if (markerIdx < 0)
            return (facts, blocked, patterns);

        var tail = finalOutput[(markerIdx + "[WORKER_RESULT]".Length)..].Trim();
        // Expected: facts=<int> blocked=<csv> patterns=<int>
        foreach (var part in tail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var value = part[(eq + 1)..];

            if (key.Equals("facts", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var f))
                facts = f;
            else if (key.Equals("blocked", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                blocked = value.Split(',', StringSplitOptions.RemoveEmptyEntries
                                          | StringSplitOptions.TrimEntries);
            // patterns count is informational — the worker writes pattern bodies to working
            // memory under <result-key>/patterns/* and the spawning agent reads them from there.
        }

        return (facts, blocked, patterns);
    }

    private async Task SaveFailureDetailsAsync(
        string taskId,
        string description,
        string reason,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        TimeSpan timeout,
        LoopDiagnostics diagnostics,
        Queue<(DateTimeOffset At, string Message)> recentProgress,
        CancellationToken ct)
    {
        try
        {
            (DateTimeOffset At, string Message)[] snapshot;
            lock (recentProgress) snapshot = recentProgress.ToArray();

            var json = SubagentRunner.BuildFailureDetailsPayload(
                taskId, description, reason, errorMessage: null,
                startedAt, elapsed, timeout, diagnostics, snapshot);

            await workingMemory.SetAsync(
                $"worker/{taskId}/failure-details", json,
                ttl: TimeSpan.FromMinutes(240),
                category: "worker-failure",
                tags: ["worker-failure", reason]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Worker {TaskId}: failed to persist failure details; primary will see only the inline reason",
                taskId);
        }
    }
}
