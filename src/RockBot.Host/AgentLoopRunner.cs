using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Llm;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// Reusable LLM tool-calling loop shared by UserMessageHandler, ScheduledTaskHandler,
/// SubagentRunner, and subagent update handlers.
/// </summary>
#pragma warning disable CS9113 // Primary constructor parameters reserved for future handler expansion
public sealed partial class AgentLoopRunner(
    ILlmClient llmClient,
    IWorkingMemory workingMemory,
    ModelBehavior modelBehavior,
    IFeedbackStore feedbackStore,
    AgentClock clock,
    IOptions<AgentHostOptions> hostOptions,
    ISkillStore skillStore,
    IEnumerable<IServiceSearchIndex> serviceSearchIndexProviders,
    IConversationMemory conversationMemory,
    ILogger<AgentLoopRunner> logger)
{
    private readonly IServiceSearchIndex? _serviceSearchIndex = serviceSearchIndexProviders.FirstOrDefault();
    private const int MaxConsecutiveTimeoutIterations = 2;

    /// <summary>
    /// Detects when a model claims to have performed tool actions in plain text without
    /// actually emitting function calls. Public so callers that pre-fetch the first
    /// response (e.g. UserMessageHandler) can apply the same check before routing.
    /// </summary>
    public static readonly Regex HallucinatedActionRegex = new(
        @"\bI(?:['\u2019]ve| have)\s+(cancell?ed|scheduled|created|updated|rescheduled|deleted|removed|completed|added|saved)\b" +
        @"|(?:Task|Subagent|Agent)\s+(?:ID|Id|id)\s*[:=]\s*\*{0,2}[a-z0-9]{8,}\*{0,2}" +  // fabricated IDs — real IDs are hex but models invent non-hex alphanum too
        @"|\bSubagent\s+\*{0,2}[a-z0-9]{8,}\*{0,2}\s+is\s+now\s+running\b" +              // "Subagent **abc123** is now running"
        @"|\bhas\s+been\s+dispatched\b" +                                                    // "has been dispatched"
        @"|\bis\s+now\s+running\s+(?:email|triage|research|the)\b",                         // "is now running email triage"
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Detects when a model claims it lacks a connected service or tool, prompting a
    /// check via mcp_list_services before giving up. Public so NativeLlmLoopAsync and
    /// the first-response routing in UserMessageHandler can apply the same check.
    /// </summary>
    public static readonly Regex CapabilityDenialRegex = new(
        @"\bI\s+(?:don['\u2019]?t|do\s+not)\s+(?:currently\s+)?have\s+(?:(?:direct\s+)?access\s+to\s+(?:a\s+|an\s+|any\s+)?|a\s+|any\s+(?:connected\s+)?)(?:calendar|email|mail|scheduling|service|tool|integration|plugin)\b" +
        @"|\bI(?:['\u2019]m|\s+am)\s+(?:not\s+able|unable)\s+to\s+(?:access|use)\s+(?:\w+\s+)?(?:calendar|email|mail|scheduling|service|tool|integration)\b" +
        @"|\bno\s+(?:calendar|email|mail|scheduling|external)\s+(?:service|tool|integration)\b" +
        @"|\bI\s+(?:don['\u2019]?t|do\s+not)\s+(?:currently\s+)?have\s+(?:the\s+)?(?:tools?|ability|capability|a\s+way)\s+to\s+(?:access|check|view|schedule|manage|connect\s+to)\b" +
        @"|\bI\s+lack\s+(?:access\s+to|a)\s+(?:calendar|email|mail|service|tool)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Detects when a follow-up pass produces a refusal or meta-commentary about
    /// the agent's own system instructions, guardrails, or internal rules — content
    /// that should be discarded rather than appended to a good response.
    /// </summary>
    private static readonly Regex FollowUpRefusalRegex = new(
        @"\bI(?:['\u2019]m|\s+am)\s+not\s+going\s+to\s+do\s+that\b" +
        @"|\bI\s+(?:can(?:not|['\u2019]t)|will\s+not|won['\u2019]t)\s+(?:extract|persist|export|expose|reveal|share)\s+(?:\w+\s+)*(?:system|developer|internal|guardrail|instruction|configuration|rule)\b" +
        @"|\b(?:system|developer)\s+instructions\s+are\s+(?:protected|confidential|private)\b" +
        @"|\bpolicy\s+leak\b" +
        @"|\bconfiguration\s+drift\b" +
        @"|\bNo\s+tools\s+were\s+invoked\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const string CapabilityDenialNudge =
        "Before concluding you lack access, check what services are available using " +
        "mcp_list_services or search_known_services, then use mcp_invoke_tool to call them.";

    /// <summary>
    /// Context window limit in tokens, learned from the first overflow error (text-based path only).
    /// </summary>
    private int? _knownContextLimit;

    // ── Completion evaluator types ─────────────────────────────────────────

    private enum LoopExitReason { ModelStopped, MaxIterationsReached, ConsecutiveTimeouts }
    private readonly record struct LoopResult(string Response, LoopExitReason ExitReason);

    /// <summary>
    /// Tracks consecutive identical (tool name, arguments, result) triples across loop
    /// iterations. When the same call keeps producing the same result <see cref="Threshold"/>
    /// times in a row the detector signals that the agent should try a different approach.
    /// Internal and sealed so it can be unit-tested directly.
    /// </summary>
    internal sealed class RepetitiveToolCallDetector
    {
        public const int Threshold = 3;

        private string? _lastKey;
        private int _count;

        /// <summary>
        /// Records the outcome of a single tool call and returns <c>true</c> when the
        /// threshold of consecutive identical call-results has been reached (and resets
        /// the internal state so the next call starts a fresh run).
        /// </summary>
        public bool Track(string toolName, string argsKey, string result)
        {
            // Truncate large results so the key stays manageable.
            var resultTrunc = result is { Length: > 500 } ? result[..500] : result;
            var key = $"{toolName}|{argsKey}|{resultTrunc}";

            if (key == _lastKey)
            {
                _count++;
            }
            else
            {
                _lastKey = key;
                _count = 1;
            }

            if (_count >= Threshold)
            {
                Reset();
                return true;
            }

            return false;
        }

        /// <summary>Resets tracking state (e.g. on a successful call).</summary>
        public void Reset()
        {
            _lastKey = null;
            _count = 0;
        }
    }

    private sealed record CompletionEvalDto(bool Complete, string? Reason);
    private sealed record FollowUpEvalDto(bool HasFollowUps, string? Prompt, string? SearchTerms);

    private const string CompletionEvaluatorPrompt =
        """
        You are a task-completion evaluator. Given an original user request and an
        agent's response, determine whether the agent fully completed the requested task.

        Rules:
        - "Complete" means the agent performed the actions requested and reported results.
        - If the agent only described what it would do, narrated a plan, or gave a partial
          answer without taking action, that is INCOMPLETE.
        - If the agent said it completed the task and the response contains evidence of
          completion (specific data, confirmation of actions taken), that is COMPLETE.
        - If the original request was a simple question and the agent answered it, that
          is COMPLETE.
        - If the agent encountered a SPECIFIC tool error (timeout, auth failure, API error)
          and explained it clearly, that is COMPLETE.
        - IMPORTANT: If the agent claimed it lacks access to a service, cannot connect,
          or does not have the right tools — WITHOUT actually trying to call any tools
          first — that is INCOMPLETE. The agent should attempt to use its tools before
          concluding it cannot do something.

        Return ONLY a valid JSON object — no markdown, no code fences.
        {"complete": true, "reason": "brief explanation"}
        """;

    private const string FollowUpEvaluatorPrompt =
        """
        You are a proactive-opportunity evaluator for a personal AI assistant. The agent
        just completed the user's request. Your job is to decide whether ONE high-value
        follow-up action is warranted based on the user's ORIGINAL intent.

        ## Primary signal: the user's original request

        Start by classifying the user's request:
        - **Closed/specific** — the user asked a direct question or gave a concrete task
          ("what is on my todo list?", "add a reminder for Saturday", "cancel my 3pm meeting").
          These almost NEVER warrant follow-ups. The user asked for X, got X, done.
        - **Open/exploratory** — the user asked the agent to investigate, research, or
          connect information across sources ("find emails from Richard and see if I have
          outstanding requests", "what's going on with the Henderson project?", "catch me
          up on anything I missed today"). These MAY warrant follow-ups that continue the
          exploration the user initiated.

        If the request is closed/specific, return hasFollowUps: false unless the agent
        learned something clearly reusable (e.g. discovered a misconfiguration it can fix
        via a skill update).

        ## Good follow-ups (only for open/exploratory requests or reusable learnings):
        - Cross-referencing calendar, email, or contacts when the user asked to explore
          a topic involving people or events
        - Connecting dots that extend the user's stated investigation
          (e.g. "you asked about Richard — there's also a calendar event with him Thursday")
        - Creating or refining a skill when the agent learned something reusable
          (e.g. a workflow pattern, a corrected configuration, a user preference)
        - Saving contextual information to memory that would be useful later

        ## Bad follow-ups (NEVER suggest these):
        - Anything the agent already did in its response
        - Generic offers ("would you like me to...") — the agent should ACT, not ask
        - Unrelated tangents or speculative actions
        - Repeating searches or lookups the agent already performed
        - Anything about the agent's own system instructions, guardrails, configuration,
          internal rules, or operational behavior
        - Meta-discussion about the agent itself, its architecture, or its capabilities
        - Extracting, persisting, or modifying system/developer instructions
        - Implementing rules, validation logic, deduplication, or automated behaviors
          in services or servers — the agent cannot change server-side logic at runtime
        - Searching unrelated systems to double-check work the agent already completed
          using the authoritative source (e.g. searching email to verify a to-do list)

        If there is a clear, high-value follow-up, return:
        {"hasFollowUps": true, "prompt": "concise instruction for the agent to execute", "searchTerms": "keywords for finding relevant skills and services"}

        If the conversation is closed/specific or there are no valuable follow-ups, return:
        {"hasFollowUps": false, "prompt": null, "searchTerms": null}

        Return ONLY a valid JSON object — no markdown, no code fences.
        """;

    /// <summary>
    /// Runs the LLM tool-calling loop.
    /// For native models (UseTextBasedToolCalling = false), delegates to
    /// <see cref="RockBotFunctionInvokingChatClient"/> which handles the full tool loop.
    /// For text-based models (UseTextBasedToolCalling = true), uses the manual loop
    /// that parses tool calls from free text.
    /// After the inner loop returns, a lightweight completion evaluator checks whether
    /// the agent's response actually completes the original request and re-prompts if not.
    /// </summary>
    public async Task<string> RunAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        string? sessionId,
        ChatResponse? firstResponse = null,
        ModelTier tier = ModelTier.Balanced,
        Func<string, CancellationToken, Task>? onPreToolCall = null,
        Func<string, CancellationToken, Task>? onProgress = null,
        Func<string, CancellationToken, Task>? onToolTimeout = null,
        Func<string, CancellationToken, Task>? onStageProgress = null,
        bool enableFollowUp = true,
        bool enableCompletionEval = true,
        double? complexityScore = null,
        int? maxIterationsOverride = null,
        CancellationToken cancellationToken = default)
    {
        using var _ = ToolCallSessionContext.Set(sessionId);
        // Set the per-async-flow override so RockBotFunctionInvokingChatClient
        // (singleton, native path) picks it up for this request.
        using var __ = MaxIterationsOverrideContext.Set(maxIterationsOverride);

        // Ensure a current datetime context is always present.
        EnsureDateTimeContext(chatMessages);

        // Inject reasoning scaffolding so the model knows its iteration budget.
        InjectReasoningScaffolding(chatMessages);

        var originalUserRequest = ExtractOriginalUserRequest(chatMessages);
        var maxReprompts = modelBehavior.MaxCompletionRepromptsOverride
            ?? hostOptions.Value.MaxCompletionReprompts;

        for (var reprompt = 0; reprompt <= maxReprompts; reprompt++)
        {
            var result = modelBehavior.UseTextBasedToolCalling
                ? await RunTextBasedLoopAsync(
                    chatMessages, chatOptions, sessionId, firstResponse, tier,
                    onPreToolCall, onProgress, onToolTimeout, cancellationToken)
                : await RunNativeLoopAsync(
                    chatMessages, chatOptions, firstResponse, tier, sessionId, cancellationToken);

            // Clear first response after the first iteration — it's already been consumed.
            firstResponse = null;

            // Skip evaluation when force-terminated due to consecutive timeouts.
            if (result.ExitReason == LoopExitReason.ConsecutiveTimeouts)
            {
                HostDiagnostics.CompletionCheckSkipped.Add(1);
                logger.LogInformation("Completion evaluator: SKIPPED (consecutive timeouts)");
                return result.Response;
            }

            // Skip evaluation when disabled or on the final re-prompt.
            if (!enableCompletionEval || maxReprompts == 0 || reprompt == maxReprompts)
                return result.Response;

            // Skip evaluation when the agent delegated to a subagent. Spawning a
            // subagent is intentional delegation — the SubagentResultHandler will
            // synthesize and publish the result. Re-prompting here would race with
            // that handler and produce duplicate answers.
            if (chatMessages.Any(m => m.Contents.OfType<FunctionCallContent>()
                    .Any(fc => fc.Name is "spawn_subagent" or "invoke_agent")))
            {
                HostDiagnostics.CompletionCheckSkipped.Add(1);
                logger.LogInformation("Completion evaluator: SKIPPED (delegated to subagent/agent)");
                return result.Response;
            }

            // Heuristic short-circuit: if the model stopped naturally and the response
            // passes basic quality checks, skip the evaluator LLM call. The hallucination
            // and capability denial patterns are the primary triggers for INCOMPLETE verdicts;
            // when neither fires, the evaluator almost always returns complete.
            if (result.ExitReason == LoopExitReason.ModelStopped
                && result.Response.Length >= 20
                && !HallucinatedActionRegex.IsMatch(result.Response)
                && !CapabilityDenialRegex.IsMatch(result.Response))
            {
                HostDiagnostics.CompletionCheckSkipped.Add(1);
                logger.LogInformation(
                    "Completion evaluator: SKIPPED (model stopped, {ResponseLen} chars, no hallucination/denial patterns)",
                    result.Response.Length);

                if (!enableFollowUp || reprompt > 0)
                {
                    HostDiagnostics.FollowUpSkipped.Add(1);
                    return result.Response;
                }

                try
                {
                    var followUpResult = await RunFollowUpPassAsync(
                        chatMessages, chatOptions, sessionId, result.Response,
                        originalUserRequest, tier, complexityScore,
                        onPreToolCall, onProgress, onToolTimeout, onStageProgress,
                        cancellationToken);

                    return followUpResult ?? result.Response;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Follow-up pass failed; returning completed response instead of propagating error");
                    return result.Response;
                }
            }

            // Full evaluator path: response may be incomplete (short, hallucinated, or denial).
            if (onStageProgress is not null)
                await onStageProgress("Reviewing response…", cancellationToken);

            var (complete, reason) = await EvaluateCompletionAsync(
                originalUserRequest, result.Response, cancellationToken);

            if (complete)
            {
                HostDiagnostics.CompletionCheckComplete.Add(1);
                logger.LogInformation(
                    "Completion evaluator: COMPLETE (reprompt {Reprompt}/{Max}) — {Reason}",
                    reprompt, maxReprompts, reason);

                // Proactive follow-up: only on first-pass completion (reprompt == 0)
                // of direct user requests (enableFollowUp == true). Subagent results,
                // scheduled tasks, A2A handlers, and feedback handlers pass false to
                // prevent cascading: SubagentResultHandler synthesis → follow-up finds
                // more work → spawns another subagent → another result → another
                // follow-up → infinite loop.
                if (!enableFollowUp || reprompt > 0)
                {
                    HostDiagnostics.FollowUpSkipped.Add(1);
                    return result.Response;
                }

                try
                {
                    var followUpResult = await RunFollowUpPassAsync(
                        chatMessages, chatOptions, sessionId, result.Response,
                        originalUserRequest, tier, complexityScore,
                        onPreToolCall, onProgress, onToolTimeout, onStageProgress,
                        cancellationToken);

                    return followUpResult ?? result.Response;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Follow-up pass failed; returning completed response instead of propagating error");
                    return result.Response;
                }
            }

            // Not complete — inject continuation and re-enter the loop.
            HostDiagnostics.CompletionCheckIncomplete.Add(1);
            logger.LogInformation(
                "Completion evaluator: INCOMPLETE (reprompt {Reprompt}/{Max}) — {Reason}",
                reprompt, maxReprompts, reason);

            if (onStageProgress is not null)
                await onStageProgress("Still working — refining response…", cancellationToken);

            chatMessages.Add(new ChatMessage(ChatRole.Assistant, result.Response));

            // Enrich context: the original user prompt may not have contained keywords
            // that match relevant skills or services (e.g. "what time do I speak tomorrow?"
            // has no overlap with "calendar"). Search using the evaluator reason + original
            // request combined, which WILL contain domain terms like "calendar", "email", etc.
            await EnrichContextForRepromptAsync(
                chatMessages, originalUserRequest, reason ?? string.Empty, cancellationToken);

            // Build a targeted continuation nudge.
            var nudge = CapabilityDenialRegex.IsMatch(result.Response)
                ? $"Not complete because: {reason}. You DO have access to external services. " +
                  "Call search_known_services or mcp_list_services to discover available integrations, " +
                  "then use mcp_invoke_tool to call the appropriate service. Do not give up without trying."
                : $"Not complete because: {reason}. Continue working on the original request. " +
                  "Use your available tools — do not claim you lack access without trying them first.";

            chatMessages.Add(new ChatMessage(ChatRole.User, nudge));
        }

        // Should not be reachable, but satisfy the compiler.
        return string.Empty;
    }

    /// <summary>
    /// Native tool-calling path: FunctionInvokingChatClient handles the full tool loop.
    /// </summary>
    private async Task<LoopResult> RunNativeLoopAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        ChatResponse? firstResponse,
        ModelTier tier,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Tool execution path: NATIVE (M.E.AI FunctionInvokingChatClient) — {MessageCount} messages in context",
            chatMessages.Count);

        // If there's a pre-fetched first response with tool calls, add it to history
        // and let the middleware continue from there.
        if (firstResponse is not null)
        {
            chatMessages.AddRange(firstResponse.Messages);
            logger.LogInformation("Added pre-fetched first response to context for native path");
        }

        ChatResponse response;
        try
        {
            response = await llmClient.GetResponseAsync(chatMessages, tier, chatOptions, cancellationToken);
        }
        catch (ClientResultException ex)
            when (ex.Status == 400 && ex.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase))
        {
            LogContentFilterDiagnostics(chatMessages, ex);
            response = await RecoverFromContentFilterAsync(
                chatMessages, tier, chatOptions, sessionId, ex, cancellationToken);
        }

        // Append response messages to chatMessages so re-prompts have full tool-call history.
        chatMessages.AddRange(response.Messages);

        var tierTag = new KeyValuePair<string, object?>("rockbot.llm.tier", tier.ToString());
        HostDiagnostics.TurnTokensInput.Record(response.Usage?.InputTokenCount ?? 0, tierTag);
        HostDiagnostics.TurnTokensOutput.Record(response.Usage?.OutputTokenCount ?? 0, tierTag);
        HostDiagnostics.TurnToolCalls.Record(
            response.Messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Count(),
            tierTag);

        return new LoopResult(ExtractAssistantText(response), LoopExitReason.ModelStopped);
    }

    /// <summary>
    /// Ensures a current datetime system message is present in <paramref name="chatMessages"/>.
    /// Replaces an existing one if found (keeps the time current for long-running loops),
    /// or inserts one after the first system message if absent.
    /// </summary>
    private void EnsureDateTimeContext(List<ChatMessage> chatMessages)
    {
        var text =
            $"The user's local date and time is: {clock.Now:dddd, MMMM d, yyyy} {clock.Now:HH:mm:ss zzz} ({clock.Zone.Id}). " +
            "Always express dates and times to the user in this timezone. Never assume UTC or any other timezone.";

        for (var i = 0; i < chatMessages.Count; i++)
        {
            if (chatMessages[i].Role == ChatRole.System &&
                chatMessages[i].Text?.StartsWith("The user's local date and time is:") == true)
            {
                chatMessages[i] = new ChatMessage(ChatRole.System, text);
                return;
            }
        }

        // Not already present — insert after the first system message (or at 0 if none)
        var insertAt = chatMessages.Count > 0 && chatMessages[0].Role == ChatRole.System ? 1 : 0;
        chatMessages.Insert(insertAt, new ChatMessage(ChatRole.System, text));
    }

    private const string ReasoningScaffoldingMarker = "You have up to ";

    /// <summary>
    /// Injects a system message that tells the model its iteration budget and encourages
    /// step-by-step reasoning before acting. Placed just before the final user message
    /// for maximum visibility in the model's context window. Idempotent — skips if already
    /// present (e.g. when RunAsync is called with a pre-built message list that already
    /// went through this path).
    /// </summary>
    private void InjectReasoningScaffolding(List<ChatMessage> chatMessages)
    {
        // Don't inject twice.
        for (var i = 0; i < chatMessages.Count; i++)
        {
            if (chatMessages[i].Role == ChatRole.System &&
                chatMessages[i].Text?.StartsWith(ReasoningScaffoldingMarker) == true)
                return;
        }

        var maxIterations = MaxIterationsOverrideContext.Value
            ?? modelBehavior.MaxToolIterationsOverride
            ?? hostOptions.Value.MaxToolIterations;

        var text =
            $"You have up to {maxIterations} tool-calling iterations available for this request. " +
            "Do not stop after one tool call if more work remains — after each result, assess whether " +
            "the task is fully complete, and if not, continue to the next step.\n\n" +
            "Before acting, think through what steps are needed to fully complete the request, then " +
            "execute them one by one without narrating your plan.";

        // Insert just before the final user message for recency in the context window.
        // Falls back to end-of-list if no user message is found.
        var insertAt = chatMessages.Count;
        for (var i = chatMessages.Count - 1; i >= 0; i--)
        {
            if (chatMessages[i].Role == ChatRole.User)
            {
                insertAt = i;
                break;
            }
        }

        chatMessages.Insert(insertAt, new ChatMessage(ChatRole.System, text));
    }

    /// <summary>
    /// Text-based tool-calling loop for models that do not support native structured
    /// tool calling (e.g. DeepSeek). Parses tool calls from free text and manually
    /// invokes tools.
    /// </summary>
    private async Task<LoopResult> RunTextBasedLoopAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        string? sessionId,
        ChatResponse? firstResponse,
        ModelTier tier,
        Func<string, CancellationToken, Task>? onPreToolCall,
        Func<string, CancellationToken, Task>? onProgress,
        Func<string, CancellationToken, Task>? onToolTimeout,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Tool execution path: TEXT-BASED (manual parsing loop) — {MessageCount} messages in context",
            chatMessages.Count);

        // Accumulate per-turn token and tool-call counts across all loop iterations.
        long totalInputTokens = firstResponse?.Usage?.InputTokenCount ?? 0;
        long totalOutputTokens = firstResponse?.Usage?.OutputTokenCount ?? 0;
        long totalToolCalls = 0;
        var tierTag = new KeyValuePair<string, object?>("rockbot.llm.tier", tier.ToString());

        ChatResponse? pendingResponse = firstResponse;
        var anyToolCalled = false;
        var maxIterations = MaxIterationsOverrideContext.Value
            ?? modelBehavior.MaxToolIterationsOverride
            ?? hostOptions.Value.MaxToolIterations;
        var consecutiveTimeoutIterations = 0;
        var repetitiveCallDetector = new RepetitiveToolCallDetector();

        try
        {
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            ChatResponse response;

            if (pendingResponse is not null)
            {
                response = pendingResponse;
                pendingResponse = null;
                logger.LogInformation("Processing pre-fetched first response in background — iteration 2");
            }
            else
            {
                if (_knownContextLimit is int preLimit)
                    TrimLargeToolResults(chatMessages, preLimit);

                logger.LogInformation("Calling LLM — iteration {Iteration} ({MessageCount} messages in context)",
                    iteration + 2, chatMessages.Count);
                var sw = Stopwatch.StartNew();

                try
                {
                    response = await llmClient.GetResponseAsync(chatMessages, tier, chatOptions, cancellationToken);
                }
                catch (ClientResultException ex)
                    when (ex.Status == 400 && TryParseContextOverflow(ex.Message, out var max, out var used))
                {
                    _knownContextLimit = max;
                    logger.LogWarning(
                        "Context overflow ({Used:N0}/{Max:N0} tokens); trimming tool results and retrying once",
                        used, max);
                    TrimLargeToolResults(chatMessages, max);
                    response = await llmClient.GetResponseAsync(chatMessages, tier, chatOptions, cancellationToken);
                }
                catch (ClientResultException ex)
                    when (ex.Status == 400 && ex.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase))
                {
                    LogContentFilterDiagnostics(chatMessages, ex);
                    response = await RecoverFromContentFilterAsync(
                        chatMessages, tier, chatOptions, sessionId, ex, cancellationToken);
                }

                sw.Stop();
                logger.LogInformation(
                    "LLM responded in {ElapsedMs}ms — {MsgCount} message(s), iteration {Iteration}",
                    sw.ElapsedMilliseconds, response.Messages.Count, iteration + 2);

                totalInputTokens += response.Usage?.InputTokenCount ?? 0;
                totalOutputTokens += response.Usage?.OutputTokenCount ?? 0;
            }

            LogResponseMessages(response, iterationLabel: (iteration + 2).ToString());

            var functionCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();
            totalToolCalls += functionCalls.Count;

            logger.LogInformation("  FunctionCallContent count: {Count}", functionCalls.Count);

            if (functionCalls.Count == 0)
            {
                var text = ExtractAssistantText(response);
                var knownTools = (chatOptions.Tools?
                    .OfType<AIFunction>()
                    .Select(t => t.Name)
                    ?? [])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var textCalls = ParseTextToolCalls(text, knownTools);
                totalToolCalls += textCalls.Count;

                if (textCalls.Count == 0)
                {
                    if (IsIncompleteSetupPhrase(text))
                    {
                        logger.LogInformation(
                            "Response looks like an incomplete setup phrase ({Length} chars); nudging LLM to continue",
                            text.Length);
                        chatMessages.Add(new ChatMessage(ChatRole.Assistant, text));
                        chatMessages.Add(new ChatMessage(ChatRole.User,
                            "Stop narrating. Emit the tool call now — do not describe what you are about to do."));
                        continue;
                    }

                    if (modelBehavior.NudgeOnHallucinatedToolCalls
                        && !anyToolCalled
                        && HallucinatedActionRegex.IsMatch(text))
                    {
                        logger.LogWarning(
                            "Hallucinated tool actions detected ({Length} chars); nudging LLM to actually call tools",
                            text.Length);
                        chatMessages.Add(new ChatMessage(ChatRole.Assistant, text));
                        chatMessages.Add(new ChatMessage(ChatRole.User,
                            "You described taking actions but no tool calls were detected. Please call the required tools now."));
                        continue;
                    }

                    if (modelBehavior.NudgeOnHallucinatedToolCalls
                        && CapabilityDenialRegex.IsMatch(text))
                    {
                        logger.LogWarning(
                            "Capability denial detected ({Length} chars); nudging LLM to check available services",
                            text.Length);
                        chatMessages.Add(new ChatMessage(ChatRole.Assistant, text));
                        chatMessages.Add(new ChatMessage(ChatRole.User, CapabilityDenialNudge));
                        continue;
                    }

                    logger.LogInformation("Final response text ({Length} chars): {Preview}",
                        text.Length, text.Length > 200 ? text[..200] + "..." : text);
                    return new LoopResult(text, LoopExitReason.ModelStopped);
                }

                logger.LogInformation(
                    "Detected {Count} text-based tool call(s) on iteration {Iteration}",
                    textCalls.Count, iteration + 2);

                var preToolText = GetPreToolText(text);
                if (!string.IsNullOrWhiteSpace(preToolText))
                    chatMessages.Add(new ChatMessage(ChatRole.Assistant, preToolText));

                foreach (var (toolName, argsJson) in textCalls)
                {
                    var tool = chatOptions.Tools?
                        .OfType<AIFunction>()
                        .FirstOrDefault(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));

                    if (tool is null)
                    {
                        var (textErrorMsg, textSuggestion) = BuildUnknownToolError(toolName, chatOptions);
                        logger.LogWarning("Text tool call references unknown tool: {Name} (suggestion: {Suggestion})",
                            toolName, textSuggestion ?? "none");
                        chatMessages.Add(new ChatMessage(ChatRole.User,
                            $"[Tool result for {toolName}]: {textErrorMsg}"));
                        continue;
                    }

                    AIFunctionArguments args;
                    try
                    {
                        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                        args = dict is not null
                            ? new AIFunctionArguments(
                                dict.ToDictionary(k => k.Key, k => ToNativeValue(k.Value)))
                            : new AIFunctionArguments();
                    }
                    catch (JsonException ex)
                    {
                        logger.LogWarning(ex, "Failed to parse tool args for {Name}: {Args}", toolName, argsJson);
                        chatMessages.Add(new ChatMessage(ChatRole.User,
                            $"[Tool result for {toolName}]: Error: invalid arguments JSON"));
                        continue;
                    }

                    Activity.Current?.AddEvent(new ActivityEvent("tool_selection_made",
                        tags: new ActivityTagsCollection { { "tool", toolName } }));
                    using var textToolActivity = HostDiagnostics.Source.StartActivity("rockbot.tool.call");
                    textToolActivity?.SetTag("rockbot.tool.name", toolName);
                    var toolSw = Stopwatch.StartNew();
                    var textToolStatus = "ok";
                    object? result;
                    try
                    {
                        result = await tool.InvokeAsync(args, cancellationToken);
                        toolSw.Stop();
                        textToolActivity?.SetTag("rockbot.tool.result_length", result?.ToString()?.Length ?? 0);
                        textToolActivity?.SetStatus(ActivityStatusCode.Ok);
                        logger.LogInformation("Text-based tool {Name} returned in {ElapsedMs}ms: {Result}",
                            toolName, toolSw.ElapsedMilliseconds, result);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        toolSw.Stop();
                        textToolStatus = ToolError.Codes.ExecutionFailed;
                        textToolActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                        logger.LogWarning(ex, "Text-based tool {Name} threw after {ElapsedMs}ms",
                            toolName, toolSw.ElapsedMilliseconds);
                        result = $"Error: {ex.Message}";

                        _ = feedbackStore.AppendAsync(new FeedbackEntry(
                            Id: Guid.NewGuid().ToString("N")[..12],
                            SessionId: sessionId ?? string.Empty,
                            SignalType: FeedbackSignalType.ToolFailure,
                            Summary: toolName,
                            Detail: ex.Message,
                            Timestamp: DateTimeOffset.UtcNow));
                    }

                    var textResultStr = result?.ToString() ?? string.Empty;
                    if (IsTimeoutResult(textResultStr)) textToolStatus = ToolError.Codes.Timeout;
                    ToolDiagnostics.InvokeDuration.Record(toolSw.Elapsed.TotalMilliseconds,
                        new KeyValuePair<string, object?>("rockbot.tool.name", toolName),
                        new KeyValuePair<string, object?>("rockbot.tool.status", textToolStatus));
                    ToolDiagnostics.Invocations.Add(1,
                        new KeyValuePair<string, object?>("rockbot.tool.name", toolName),
                        new KeyValuePair<string, object?>("rockbot.tool.status", textToolStatus));

                    // Chunking is handled by ChunkingAIFunction wrapper on the tool itself.
                    chatMessages.Add(new ChatMessage(ChatRole.User,
                        $"[Tool result for {toolName}]: {textResultStr}"));

                    if (repetitiveCallDetector.Track(toolName, argsJson, textResultStr))
                    {
                        logger.LogWarning(
                            "Detected {Threshold} consecutive identical tool call results for {Tool}; " +
                            "nudging LLM to try a different approach",
                            RepetitiveToolCallDetector.Threshold, toolName);
                        chatMessages.Add(new ChatMessage(ChatRole.User,
                            $"You have called {toolName} with the same arguments " +
                            $"{RepetitiveToolCallDetector.Threshold} times and received the same result " +
                            "each time. Please try a different approach."));
                    }
                }

                if (onProgress is not null)
                {
                    var descriptions = textCalls.Select(t => DescribeToolCall(t.Name, t.ArgsJson));
                    await onProgress(string.Join("; ", descriptions), cancellationToken);
                }

                continue;
            }

            logger.LogInformation(
                "LLM requested {Count} tool call(s) on iteration {Iteration}",
                functionCalls.Count, iteration + 2);

            anyToolCalled = true;
            chatMessages.AddRange(response.Messages);

            // Notify caller what tools are about to run so users see activity immediately.
            if (onPreToolCall is not null)
            {
                var preDescriptions = functionCalls.Select(DescribeToolCall);
                await onPreToolCall(string.Join("; ", preDescriptions), cancellationToken);
            }

            var iterationHadTimeout = false;

            foreach (var fc in functionCalls)
            {
                var argsSummary = fc.Arguments is not null
                    ? string.Join(", ", fc.Arguments.Select(a => $"{a.Key}={a.Value}"))
                    : "(none)";
                logger.LogInformation("Executing tool {Name}(callId={CallId}, args={Args})",
                    fc.Name, fc.CallId, argsSummary);

                var tool = chatOptions.Tools?
                    .OfType<AIFunction>()
                    .FirstOrDefault(t => t.Name.Equals(fc.Name, StringComparison.OrdinalIgnoreCase));

                if (tool is null)
                {
                    var (errorMsg, suggestion) = BuildUnknownToolError(fc.Name, chatOptions);
                    logger.LogWarning("LLM requested unknown tool: {Name} (suggestion: {Suggestion})",
                        fc.Name, suggestion ?? "none");
                    chatMessages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(fc.CallId, errorMsg)]));
                    continue;
                }

                var args = fc.Arguments is not null
                    ? new AIFunctionArguments(fc.Arguments!)
                    : new AIFunctionArguments();
                Activity.Current?.AddEvent(new ActivityEvent("tool_selection_made",
                    tags: new ActivityTagsCollection { { "tool", fc.Name } }));
                using var toolActivity = HostDiagnostics.Source.StartActivity("rockbot.tool.call");
                toolActivity?.SetTag("rockbot.tool.name", fc.Name);
                var toolSw = Stopwatch.StartNew();
                var toolStatus = "ok";
                object? result;
                try
                {
                    result = await tool.InvokeAsync(args, cancellationToken);
                    toolSw.Stop();
                    toolActivity?.SetTag("rockbot.tool.result_length", result?.ToString()?.Length ?? 0);
                    toolActivity?.SetStatus(ActivityStatusCode.Ok);
                    logger.LogInformation("Tool {Name} returned in {ElapsedMs}ms: {Result}",
                        fc.Name, toolSw.ElapsedMilliseconds, result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    toolSw.Stop();
                    toolStatus = ToolError.Codes.ExecutionFailed;
                    toolActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    logger.LogWarning(ex, "Tool {Name} threw after {ElapsedMs}ms",
                        fc.Name, toolSw.ElapsedMilliseconds);
                    result = $"Error: {ex.Message}";

                    _ = feedbackStore.AppendAsync(new FeedbackEntry(
                        Id: Guid.NewGuid().ToString("N")[..12],
                        SessionId: sessionId ?? string.Empty,
                        SignalType: FeedbackSignalType.ToolFailure,
                        Summary: fc.Name,
                        Detail: ex.Message,
                        Timestamp: DateTimeOffset.UtcNow));
                }

                // Chunking is handled by ChunkingAIFunction wrapper on the tool itself.
                var nativeResultStr = result?.ToString() ?? string.Empty;
                if (IsTimeoutResult(nativeResultStr)) toolStatus = ToolError.Codes.Timeout;
                ToolDiagnostics.InvokeDuration.Record(toolSw.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("rockbot.tool.name", fc.Name),
                    new KeyValuePair<string, object?>("rockbot.tool.status", toolStatus));
                ToolDiagnostics.Invocations.Add(1,
                    new KeyValuePair<string, object?>("rockbot.tool.name", fc.Name),
                    new KeyValuePair<string, object?>("rockbot.tool.status", toolStatus));
                chatMessages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(fc.CallId, nativeResultStr)]));

                if (repetitiveCallDetector.Track(fc.Name, argsSummary, nativeResultStr))
                {
                    logger.LogWarning(
                        "Detected {Threshold} consecutive identical tool call results for {Tool}; " +
                        "nudging LLM to try a different approach",
                        RepetitiveToolCallDetector.Threshold, fc.Name);
                    chatMessages.Add(new ChatMessage(ChatRole.User,
                        $"You have called {fc.Name} with the same arguments " +
                        $"{RepetitiveToolCallDetector.Threshold} times and received the same result " +
                        "each time. Please try a different approach."));
                }

                if (IsTimeoutResult(nativeResultStr))
                {
                    iterationHadTimeout = true;
                    if (onToolTimeout is not null)
                        await onToolTimeout(DescribeToolCall(fc), cancellationToken);
                }
            }

            // Track consecutive timeout iterations to detect a stalled service.
            if (iterationHadTimeout)
            {
                consecutiveTimeoutIterations++;
                if (consecutiveTimeoutIterations >= MaxConsecutiveTimeoutIterations)
                {
                    logger.LogWarning(
                        "Aborting tool loop: {N} consecutive iterations with tool timeouts",
                        consecutiveTimeoutIterations);
                    return new LoopResult(
                        "I wasn't able to complete this task — the services I need aren't responding right now. " +
                        "Please try again in a few minutes.",
                        LoopExitReason.ConsecutiveTimeouts);
                }
            }
            else
            {
                consecutiveTimeoutIterations = 0;
            }

            if (onProgress is not null)
            {
                var descriptions = functionCalls.Select(DescribeToolCall);
                await onProgress(string.Join("; ", descriptions), cancellationToken);
            }

            if (iteration == maxIterations - 2)
                chatOptions = new ChatOptions();
        }

        logger.LogWarning("Tool loop reached {Max} iterations; forcing final response", maxIterations);

        // Ask the LLM for a backward-looking summary so it reports what was done, not what
        // it was planning to do next. Without this nudge the model often produces setup
        // phrases ("Now let me save…", "Next I will…") that are useless as a final reply.
        var summaryMessages = new List<ChatMessage>(chatMessages)
        {
            new(ChatRole.User,
                "The task loop has ended. Write a concise summary of what was accomplished. " +
                "Report only what was completed — do not describe intentions or future actions.")
        };

        var finalResponse = await llmClient.GetResponseAsync(
            summaryMessages, tier, new ChatOptions(), cancellationToken);
        var forcedText = ExtractAssistantText(finalResponse);

        // If the forced response is itself an incomplete setup phrase, nudge once more.
        if (!string.IsNullOrWhiteSpace(forcedText) && IsIncompleteSetupPhrase(forcedText))
        {
            logger.LogWarning(
                "Forced final response was an incomplete setup phrase ({Length} chars); nudging for clean summary",
                forcedText.Length);
            summaryMessages.Add(new ChatMessage(ChatRole.Assistant, forcedText));
            summaryMessages.Add(new ChatMessage(ChatRole.User,
                "Do not narrate intentions. Summarise only what was completed."));
            var nudgedResponse = await llmClient.GetResponseAsync(
                summaryMessages, tier, new ChatOptions(), cancellationToken);
            forcedText = ExtractAssistantText(nudgedResponse);
        }

        if (!string.IsNullOrWhiteSpace(forcedText))
            return new LoopResult(forcedText, LoopExitReason.MaxIterationsReached);

        // The forced final response had no usable text (model returned only tool calls or
        // an empty message). Fall back to the last non-empty assistant turn in history so
        // the caller still receives a meaningful result rather than an empty string.
        for (var i = chatMessages.Count - 1; i >= 0; i--)
        {
            var m = chatMessages[i];
            if (m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))
            {
                var fallback = StripModelToolTokens(m.Text).Trim();
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    logger.LogWarning(
                        "Forced final response was empty; using last assistant turn from history ({Len} chars)",
                        fallback.Length);
                    return new LoopResult(fallback, LoopExitReason.MaxIterationsReached);
                }
            }
        }

        logger.LogWarning("Forced final response empty and no usable assistant history found; returning empty string");
        return new LoopResult(string.Empty, LoopExitReason.MaxIterationsReached);
        } // end try
        finally
        {
            HostDiagnostics.TurnTokensInput.Record(totalInputTokens, tierTag);
            HostDiagnostics.TurnTokensOutput.Record(totalOutputTokens, tierTag);
            HostDiagnostics.TurnToolCalls.Record(totalToolCalls, tierTag);
        }
    }

    // ── Context overflow handling (text-based path only) ──────────────────────

    private void TrimLargeToolResults(List<ChatMessage> messages, int maxTokens)
    {
        const int CharsPerToken = 4;
        var charBudget = (int)(maxTokens * CharsPerToken * 0.9);

        while (true)
        {
            var totalChars = messages.Sum(EstimateMessageChars);
            if (totalChars <= charBudget)
                break;

            int bestMsg = -1, bestContent = -1, bestLen = 0;
            for (var i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role != ChatRole.Tool) continue;
                for (var j = 0; j < messages[i].Contents.Count; j++)
                {
                    if (messages[i].Contents[j] is FunctionResultContent frc)
                    {
                        var len = frc.Result?.ToString()?.Length ?? 0;
                        if (len > bestLen) { bestMsg = i; bestContent = j; bestLen = len; }
                    }
                }
            }

            if (bestMsg < 0)
                break;

            var old = (FunctionResultContent)messages[bestMsg].Contents[bestContent];
            var oldStr = old.Result?.ToString() ?? string.Empty;
            var excess = totalChars - charBudget;
            var targetLen = Math.Max(200, oldStr.Length - excess - 60);
            var trimmed = oldStr[..targetLen] + "\n[truncated to fit context window]";

            messages[bestMsg].Contents[bestContent] = new FunctionResultContent(old.CallId, trimmed);

            logger.LogInformation(
                "Trimmed tool result for call {CallId}: {Before:N0} → {After:N0} chars",
                old.CallId, bestLen, trimmed.Length);
        }
    }

    private static int EstimateMessageChars(ChatMessage m) =>
        m.Contents.Sum(static c => c switch
        {
            TextContent tc => tc.Text?.Length ?? 0,
            FunctionResultContent frc => frc.Result?.ToString()?.Length ?? 0,
            _ => 50
        });

    private static bool TryParseContextOverflow(string message, out int maxTokens, out int usedTokens)
    {
        maxTokens = 0;
        usedTokens = 0;

        var maxMatch = Regex.Match(message, @"maximum context length is (\d+)");
        var usedMatch = Regex.Match(message, @"resulted in (\d+) tokens");

        if (!maxMatch.Success || !usedMatch.Success)
            return false;

        maxTokens = int.Parse(maxMatch.Groups[1].Value);
        usedTokens = int.Parse(usedMatch.Groups[1].Value);
        return true;
    }

    // ── Content filter recovery ───────────────────────────────────────────────

    /// <summary>
    /// Attempts to recover from an Azure content filter rejection by stripping conversation
    /// history (which may contain a poisonous message from a prior turn) and retrying with
    /// only system messages and the current user request. On success, clears the session's
    /// conversation memory so the offending turn doesn't re-poison future requests.
    /// </summary>
    private async Task<ChatResponse> RecoverFromContentFilterAsync(
        List<ChatMessage> chatMessages,
        ModelTier tier,
        ChatOptions chatOptions,
        string? sessionId,
        ClientResultException originalException,
        CancellationToken cancellationToken)
    {
        var stripped = StripConversationHistory(chatMessages);
        if (stripped == 0)
        {
            // Nothing to strip — the current message itself is the problem.
            logger.LogWarning("Content filter triggered with no conversation history to strip; cannot recover");
            throw originalException;
        }

        logger.LogWarning(
            "Content filter recovery: stripped {Removed} history message(s), retrying with {Remaining} messages",
            stripped, chatMessages.Count);

        try
        {
            var response = await llmClient.GetResponseAsync(chatMessages, tier, chatOptions, cancellationToken);

            // Retry succeeded — the poison was in conversation history. Clean the session memory
            // so the offending turn(s) don't re-poison the next request.
            if (sessionId is not null)
            {
                await CleanSessionMemoryAfterContentFilterAsync(sessionId, cancellationToken);
            }

            logger.LogInformation("Content filter recovery succeeded after stripping history");
            return response;
        }
        catch (ClientResultException retryEx)
            when (retryEx.Status == 400 && retryEx.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase))
        {
            // Still filtered after stripping history — the current user message is the trigger.
            logger.LogWarning("Content filter triggered again after stripping history; current message is the cause");
            throw;
        }
    }

    /// <summary>
    /// Removes conversation history (user and assistant messages that aren't the current request)
    /// from the chat message list. Keeps system messages, tool messages, and the last user message.
    /// Returns the number of messages removed.
    /// </summary>
    internal static int StripConversationHistory(List<ChatMessage> chatMessages)
    {
        // Find the last user message — this is the current request.
        var lastUserIdx = -1;
        for (var i = chatMessages.Count - 1; i >= 0; i--)
        {
            if (chatMessages[i].Role == ChatRole.User)
            {
                lastUserIdx = i;
                break;
            }
        }

        if (lastUserIdx < 0) return 0;

        var removed = 0;
        for (var i = chatMessages.Count - 1; i >= 0; i--)
        {
            if (i == lastUserIdx) continue;

            var role = chatMessages[i].Role;
            if (role == ChatRole.User || role == ChatRole.Assistant)
            {
                chatMessages.RemoveAt(i);
                removed++;
                if (i < lastUserIdx) lastUserIdx--;
            }
        }

        return removed;
    }

    /// <summary>
    /// Clears conversation memory for a session after a successful content filter recovery,
    /// keeping only the most recent user turn so the poisoned history doesn't come back.
    /// </summary>
    private async Task CleanSessionMemoryAfterContentFilterAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var turns = await conversationMemory.GetTurnsAsync(sessionId, ct);
            ConversationTurn? lastUserTurn = null;
            for (var i = turns.Count - 1; i >= 0; i--)
            {
                if (turns[i].Role == "user")
                {
                    lastUserTurn = turns[i];
                    break;
                }
            }

            await conversationMemory.ClearAsync(sessionId, ct);

            if (lastUserTurn is not null)
            {
                await conversationMemory.AddTurnAsync(sessionId, lastUserTurn, ct);
            }

            logger.LogWarning(
                "Cleared conversation memory for session {SessionId} after content filter recovery " +
                "(kept last user turn only)", sessionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to clean conversation memory for session {SessionId} after content filter recovery",
                sessionId);
        }
    }

    // ── Logging ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Dumps the full chat message context when Azure's content filter rejects the prompt,
    /// so we can identify the offending text after the fact.
    /// </summary>
    private void LogContentFilterDiagnostics(List<ChatMessage> chatMessages, ClientResultException ex)
    {
        var roleCounts = chatMessages
            .GroupBy(m => m.Role.Value)
            .Select(g => $"{g.Key}={g.Count()}")
            .ToArray();

        logger.LogWarning(ex,
            "Content filter triggered on prompt. Message breakdown: {Roles} ({Total} total)",
            string.Join(", ", roleCounts), chatMessages.Count);

        const int maxTextLen = 600;

        for (var i = 0; i < chatMessages.Count; i++)
        {
            var msg = chatMessages[i];
            var text = msg.Text ?? string.Empty;

            // For tool results, also surface the function name from FunctionResultContent.
            var functionInfo = string.Empty;
            var frc = msg.Contents.OfType<FunctionResultContent>().FirstOrDefault();
            if (frc is not null)
                functionInfo = $" fn={frc.CallId}";

            var truncated = text.Length > maxTextLen
                ? $"{text[..maxTextLen]}... [{text.Length} chars total]"
                : text;

            logger.LogWarning(
                "  ContentFilter[{Index}] role={Role}{FunctionInfo} ({Length} chars): {Text}",
                i, msg.Role, functionInfo, text.Length, truncated);
        }
    }

    private void LogResponseMessages(ChatResponse response, string iterationLabel)
    {
        for (var i = 0; i < response.Messages.Count; i++)
        {
            var msg = response.Messages[i];
            var contentParts = string.Join(", ", msg.Contents.Select(c => c.GetType().Name));
            logger.LogInformation(
                "  Message[{Index}] role={Role} text={TextLen} chars, contents=[{ContentParts}]",
                i, msg.Role, msg.Text?.Length ?? 0, contentParts);
        }
    }

    // ── Text-based tool call parsing ─────────────────────────────────────────

    public List<(string Name, string ArgsJson)> ParseTextToolCalls(string text, IReadOnlySet<string> knownTools)
    {
        var results = new List<(string, string)>();
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var cleanLine = line.TrimStart('`').Trim();

            if (cleanLine.StartsWith("tool_call_name:", StringComparison.OrdinalIgnoreCase))
            {
                var afterName = cleanLine["tool_call_name:".Length..].Trim();
                if (string.IsNullOrEmpty(afterName))
                    continue;

                string toolName;
                var argsJson = "{}";

                var sameLineArgsIdx = afterName.IndexOf("tool_call_arguments:", StringComparison.OrdinalIgnoreCase);
                if (sameLineArgsIdx >= 0)
                {
                    toolName = afterName[..sameLineArgsIdx].Trim();
                    argsJson = afterName[(sameLineArgsIdx + "tool_call_arguments:".Length)..].Trim().TrimEnd('`').Trim();
                }
                else
                {
                    toolName = afterName;

                    for (var j = i + 1; j < Math.Min(i + 4, lines.Length); j++)
                    {
                        var argsLine = lines[j].Trim();
                        if (!argsLine.StartsWith("tool_call_arguments:", StringComparison.OrdinalIgnoreCase))
                            continue;

                        argsJson = argsLine["tool_call_arguments:".Length..].Trim().TrimEnd('`').Trim();

                        if (argsJson.StartsWith("{") && !IsBalancedJson(argsJson))
                        {
                            var sb = new StringBuilder(argsJson);
                            for (var k = j + 1; k < lines.Length; k++)
                            {
                                sb.Append('\n').Append(lines[k]);
                                if (IsBalancedJson(sb.ToString()))
                                    break;
                            }
                            argsJson = sb.ToString();
                        }

                        i = j;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(toolName))
                    continue;

                logger.LogDebug("Parsed tool_call_name format: {Name}({Args})", toolName, argsJson);
                results.Add((toolName, argsJson));
            }
            else if (knownTools.Contains(cleanLine))
            {
                var argsJson = "{}";

                if (i + 1 < lines.Length)
                {
                    var nextLine = lines[i + 1].Trim();
                    if (nextLine.StartsWith("{") && IsBalancedJson(nextLine))
                    {
                        argsJson = nextLine;
                        i++;
                    }
                }

                logger.LogDebug("Parsed bare tool name format: {Name}({Args})", line, argsJson);
                results.Add((line, argsJson));
            }
        }

        return results;
    }

    public static bool IsIncompleteSetupPhrase(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.EndsWith(':') || trimmed.EndsWith("...");
    }

    public static bool IsBalancedJson(string s)
    {
        var depth = 0;
        var hasOpen = false;
        foreach (var c in s)
        {
            if (c == '{') { depth++; hasOpen = true; }
            else if (c == '}') depth--;
        }
        return hasOpen && depth == 0;
    }

    public static string GetPreToolText(string text)
    {
        var idx = text.IndexOf("tool_call_name:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text;
        if (idx == 0) return string.Empty;

        while (idx > 0 && (text[idx - 1] == '`' || text[idx - 1] == ' '))
            idx--;

        return idx <= 0 ? string.Empty : text[..idx].TrimEnd();
    }

    // ── Text extraction ─────────────────────────────────────────────────────

    public string ExtractAssistantText(ChatResponse response)
    {
        for (var i = response.Messages.Count - 1; i >= 0; i--)
        {
            var msg = response.Messages[i];
            if (msg.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(msg.Text))
                return StripModelToolTokens(msg.Text).Trim();
        }

        if (!string.IsNullOrWhiteSpace(response.Text))
            return StripModelToolTokens(response.Text).Trim();

        logger.LogWarning("LLM response contained no usable text across {Count} messages",
            response.Messages.Count);
        return string.Empty;
    }

    private static string StripModelToolTokens(string text)
    {
        // DeepSeek tool-call boundary tokens
        const string begin = "<｜tool▁calls▁begin｜>";
        var idx = text.IndexOf(begin, StringComparison.Ordinal);
        if (idx >= 0) text = text[..idx];

        // GPT-5.x reasoning tokens (startthought:意> ... endthought:意>)
        text = StripReasoningTokensRegex().Replace(text, "");

        return text;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?:start|end)thought:[^\s>]*>",
        System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex StripReasoningTokensRegex();

    // ── Tool call description ────────────────────────────────────────────────

    /// <summary>
    /// Builds a human-readable description of a tool call, including key arguments
    /// (query, url) where present, so progress messages carry real information.
    /// </summary>
    private static string DescribeToolCall(FunctionCallContent fc)
    {
        var argJson = fc.Arguments is { Count: > 0 }
            ? JsonSerializer.Serialize(fc.Arguments.ToDictionary(k => k.Key, k => k.Value))
            : null;
        return DescribeToolCall(fc.Name, argJson);
    }

    /// <summary>
    /// Builds an error message for an unknown tool, optionally suggesting the closest
    /// registered tool name via fuzzy matching.
    /// </summary>
    private static (string ErrorMessage, string? Suggestion) BuildUnknownToolError(
        string requestedName,
        ChatOptions? chatOptions)
    {
        var toolNames = chatOptions?.Tools?
            .OfType<AIFunction>()
            .Select(t => t.Name) ?? [];
        var suggestion = ToolCallFailureClassifier.FindClosestToolName(requestedName, toolNames);
        var errorMsg = suggestion is not null
            ? $"Error: unknown tool '{requestedName}'. Did you mean '{suggestion}'?"
            : $"Error: unknown tool '{requestedName}'";
        return (errorMsg, suggestion);
    }

    /// <summary>
    /// Returns true when a tool result string indicates the call timed out
    /// rather than completing successfully or failing with a non-timeout error.
    /// </summary>
    private static bool IsTimeoutResult(string result) =>
        result.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    private static string DescribeToolCall(string name, string? argsJson)
    {
        if (string.IsNullOrEmpty(argsJson)) return name;
        try
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
            if (args is null) return name;

            // mcp_invoke_tool: show "server/tool" for clarity
            if (name.Equals("mcp_invoke_tool", StringComparison.OrdinalIgnoreCase))
            {
                var server = args.TryGetValue("server_name", out var sn) ? sn.GetString() : null;
                var toolName = args.TryGetValue("tool_name", out var tn) ? tn.GetString() : null;
                if (server is not null && toolName is not null) return $"{server}/{toolName}";
                if (toolName is not null) return $"mcp_invoke_tool({toolName})";
                return name;
            }

            // Extract the most useful single argument for progress display
            var hint = args.TryGetValue("query", out var q) ? q.GetString()
                : args.TryGetValue("url", out var u) ? u.GetString()
                : args.TryGetValue("key", out var k) ? k.GetString()
                : args.TryGetValue("tool_name", out var tn2) ? tn2.GetString()
                : null;

            if (hint is null) return name;

            const int maxLen = 80;
            if (hint.Length > maxLen) hint = hint[..maxLen] + "…";
            return $"{name}({hint})";
        }
        catch
        {
            return name;
        }
    }

    public static object? ToNativeValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        _ => (object)element
    };

    // ── Follow-up passes ──────────────────────────────────────────────────

    /// <summary>
    /// After the agent completes the user's request, evaluates whether there are
    /// proactive follow-up actions worth taking. If so, enriches context with relevant
    /// skills/services and runs the tool loop once more. Returns the combined response
    /// (original + follow-up) or null if no follow-up was taken.
    /// </summary>
    private async Task<string?> RunFollowUpPassAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        string? sessionId,
        string completedResponse,
        string originalUserRequest,
        ModelTier tier,
        double? complexityScore,
        Func<string, CancellationToken, Task>? onPreToolCall,
        Func<string, CancellationToken, Task>? onProgress,
        Func<string, CancellationToken, Task>? onToolTimeout,
        Func<string, CancellationToken, Task>? onStageProgress,
        CancellationToken cancellationToken)
    {
        var maxFollowUps = modelBehavior.MaxFollowUpPassesOverride
            ?? hostOptions.Value.MaxFollowUpPasses;

        if (maxFollowUps <= 0)
        {
            HostDiagnostics.FollowUpSkipped.Add(1);
            return null;
        }

        // Short-circuit: low-complexity (closed/specific) requests almost never
        // warrant follow-ups. Skip the evaluator LLM call entirely.
        if (complexityScore.HasValue && complexityScore.Value <= 0.15)
        {
            HostDiagnostics.FollowUpSkipped.Add(1);
            logger.LogInformation(
                "Follow-up evaluator: SKIPPED (low complexity score {Score:F3})",
                complexityScore.Value);
            return null;
        }

        if (onStageProgress is not null)
            await onStageProgress("Checking for follow-up actions…", cancellationToken);

        // Build a summary of the conversation for the evaluator — include the last few
        // turns for context, not just the final response.
        var recentContext = BuildRecentConversationSummary(chatMessages);

        var followUp = await EvaluateFollowUpAsync(
            originalUserRequest, completedResponse, recentContext, cancellationToken);

        if (followUp is null || !followUp.HasFollowUps || string.IsNullOrWhiteSpace(followUp.Prompt))
        {
            HostDiagnostics.FollowUpNone.Add(1);
            logger.LogInformation("Follow-up evaluator: no proactive opportunities found");
            return null;
        }

        HostDiagnostics.FollowUpTriggered.Add(1);
        logger.LogInformation(
            "Follow-up evaluator: found opportunity — {Prompt}", followUp.Prompt);

        // Enrich context with skills/services relevant to the follow-up.
        var searchTerms = followUp.SearchTerms ?? followUp.Prompt;
        await EnrichContextForRepromptAsync(
            chatMessages, originalUserRequest, searchTerms, cancellationToken);

        // Inject the completed response and the follow-up instruction.
        chatMessages.Add(new ChatMessage(ChatRole.Assistant, completedResponse));
        chatMessages.Add(new ChatMessage(ChatRole.User,
            $"Good — you completed my request. Now, while you're in this context, " +
            $"also do this: {followUp.Prompt}\n\n" +
            "Use whatever tools are appropriate to accomplish this. " +
            "Do not claim you lack access without trying. " +
            "Report what you found concisely."));

        // Run one more pass through the tool loop. Track message count so we can
        // detect whether any tool calls were actually made during the pass.
        var preFollowUpMessageCount = chatMessages.Count;

        var result = modelBehavior.UseTextBasedToolCalling
            ? await RunTextBasedLoopAsync(
                chatMessages, chatOptions, sessionId, null, tier,
                onPreToolCall, onProgress, onToolTimeout, cancellationToken)
            : await RunNativeLoopAsync(
                chatMessages, chatOptions, null, tier, sessionId, cancellationToken);

        // Native path: FunctionCallContent in response messages.
        // Text-based path: tool results appear as "[Tool result for ...]" user messages.
        var addedMessages = chatMessages.Skip(preFollowUpMessageCount);
        var followUpToolCalls = addedMessages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Count();
        if (followUpToolCalls == 0)
        {
            followUpToolCalls = addedMessages
                .Count(m => m.Role == ChatRole.User
                    && m.Text?.StartsWith("[Tool result for ", StringComparison.Ordinal) == true);
        }

        logger.LogInformation(
            "Follow-up pass complete — {TextLen} chars, {ToolCalls} tool call(s)",
            result.Response.Length, followUpToolCalls);

        // Discard follow-up passes that didn't actually invoke any tools — these are
        // pure narration, refusals, or re-statements of the original answer. A useful
        // follow-up should have called at least one tool to gather new information.
        if (followUpToolCalls == 0)
        {
            logger.LogWarning(
                "Follow-up pass made no tool calls ({TextLen} chars); discarding as commentary",
                result.Response.Length);
            return null;
        }

        // Discard follow-up responses that are capability denials, refusals, or
        // meta-commentary about the agent's own rules/instructions rather than
        // actionable results. These add noise to an otherwise complete response.
        if (CapabilityDenialRegex.IsMatch(result.Response))
        {
            logger.LogWarning(
                "Follow-up pass produced a capability denial ({TextLen} chars); discarding",
                result.Response.Length);
            return null;
        }

        if (FollowUpRefusalRegex.IsMatch(result.Response))
        {
            logger.LogWarning(
                "Follow-up pass produced a refusal or meta-commentary ({TextLen} chars); discarding",
                result.Response.Length);
            return null;
        }

        // Combine the original response with the follow-up.
        return $"{completedResponse}\n\n{result.Response}";
    }

    /// <summary>
    /// Uses a <see cref="ModelTier.Low"/> LLM call to assess whether there are proactive
    /// follow-up opportunities in the current conversation context. Fails open (returns null)
    /// on any error.
    /// </summary>
    private async Task<FollowUpEvalDto?> EvaluateFollowUpAsync(
        string originalUserRequest, string agentResponse, string recentContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, FollowUpEvaluatorPrompt),
                new(ChatRole.User,
                    $"## Original user request\n{originalUserRequest}\n\n" +
                    $"## Recent conversation context\n{recentContext}\n\n" +
                    $"## Agent's completed response\n{agentResponse}")
            };

            var response = await llmClient.GetResponseAsync(
                messages, ModelTier.Low, new ChatOptions(), cancellationToken);
            var raw = response.Text?.Trim() ?? string.Empty;
            var json = ExtractJsonObject(raw);

            if (string.IsNullOrEmpty(json))
            {
                logger.LogWarning("Follow-up evaluator: no parseable JSON; skipping");
                return null;
            }

            return JsonSerializer.Deserialize<FollowUpEvalDto>(json, s_evalJsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Follow-up evaluator failed; skipping");
            return null;
        }
    }

    /// <summary>
    /// Builds a brief summary of recent conversation turns for the follow-up evaluator,
    /// so it has context beyond just the final response.
    /// </summary>
    private static string BuildRecentConversationSummary(List<ChatMessage> chatMessages)
    {
        var sb = new StringBuilder();
        // Take the last 6 user/assistant messages for context.
        var relevant = chatMessages
            .Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
            .TakeLast(6);
        foreach (var msg in relevant)
        {
            var role = msg.Role == ChatRole.User ? "User" : "Agent";
            var text = msg.Text ?? string.Empty;
            if (text.Length > 300)
                text = text[..300] + "...";
            sb.AppendLine($"[{role}]: {text}");
        }
        return sb.ToString();
    }

    // ── Completion evaluation ───────────────────────────────────────────────

    /// <summary>
    /// Extracts the original user request from chat history by scanning backward
    /// for the last <see cref="ChatRole.User"/> message.
    /// </summary>
    private static string ExtractOriginalUserRequest(List<ChatMessage> chatMessages)
    {
        for (var i = chatMessages.Count - 1; i >= 0; i--)
        {
            if (chatMessages[i].Role == ChatRole.User && !string.IsNullOrWhiteSpace(chatMessages[i].Text))
                return chatMessages[i].Text!;
        }

        return string.Empty;
    }

    /// <summary>
    /// Uses a cheap <see cref="ModelTier.Low"/> LLM call to evaluate whether the agent's
    /// response actually completes the original user request. Fails open (returns complete)
    /// on any error so it never blocks the response pipeline.
    /// </summary>
    private async Task<(bool Complete, string? Reason)> EvaluateCompletionAsync(
        string originalUserRequest, string agentResponse, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(originalUserRequest) || string.IsNullOrWhiteSpace(agentResponse))
            return (true, "empty request or response");

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, CompletionEvaluatorPrompt),
                new(ChatRole.User,
                    $"## Original user request\n{originalUserRequest}\n\n## Agent response\n{agentResponse}")
            };

            var response = await llmClient.GetResponseAsync(
                messages, ModelTier.Low, new ChatOptions(), cancellationToken);
            var raw = response.Text?.Trim() ?? string.Empty;
            var json = ExtractJsonObject(raw);

            if (string.IsNullOrEmpty(json))
            {
                logger.LogWarning("Completion evaluator: no parseable JSON in response; defaulting to complete");
                return (true, "evaluator returned no JSON");
            }

            var dto = JsonSerializer.Deserialize<CompletionEvalDto>(json, s_evalJsonOptions);
            if (dto is null)
            {
                logger.LogWarning("Completion evaluator: failed to deserialize JSON; defaulting to complete");
                return (true, "evaluator JSON deserialization failed");
            }

            return (dto.Complete, dto.Reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Completion evaluator failed; defaulting to complete");
            return (true, "evaluator error");
        }
    }

    private static readonly JsonSerializerOptions s_evalJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// On re-prompt, searches for skills and services using the evaluator reason combined
    /// with the original request. This catches cases where the original user prompt had no
    /// keyword overlap with relevant skills/services (e.g. "what time do I speak tomorrow?"
    /// doesn't match "calendar"), but the evaluator reason DOES contain domain terms.
    /// </summary>
    private async Task EnrichContextForRepromptAsync(
        List<ChatMessage> chatMessages,
        string originalUserRequest,
        string evaluatorReason,
        CancellationToken ct)
    {
        // Combine original request + evaluator reason for broader BM25 matching.
        var enrichedQuery = $"{originalUserRequest} {evaluatorReason}";
        var injectedAny = false;

        // Search for relevant skills
        try
        {
            var skills = await skillStore.SearchAsync(enrichedQuery, maxResults: 3, ct);
            if (skills.Count > 0)
            {
                var sb = new StringBuilder("Relevant skills for this task:\n");
                foreach (var skill in skills)
                {
                    sb.AppendLine($"\n## Skill: {skill.Name}");
                    sb.AppendLine(skill.Content);
                }
                chatMessages.Add(new ChatMessage(ChatRole.System, sb.ToString()));
                logger.LogInformation(
                    "Completion re-prompt: injected {Count} skill(s) via enriched search: {Skills}",
                    skills.Count, string.Join(", ", skills.Select(s => s.Name)));
                injectedAny = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Completion re-prompt: skill search failed (non-fatal)");
        }

        // Search for relevant services (MCP servers + A2A agents)
        if (_serviceSearchIndex is not null)
        {
            try
            {
                var candidates = _serviceSearchIndex.Search(enrichedQuery, maxResults: 3);
                if (candidates.Count > 0)
                {
                    var sb = new StringBuilder(
                        "Available services relevant to this task:\n");
                    foreach (var c in candidates)
                    {
                        sb.AppendLine($"\n### {c.Id} ({c.Type}): {c.Summary}");
                        if (c.Type == "mcp")
                        {
                            sb.AppendLine($"  Sample tools: {string.Join(", ", c.TopItems)}");
                            sb.AppendLine($"  IMPORTANT: Call mcp_get_service_details(server_name=\"{c.Id}\") " +
                                "to see ALL available tools before invoking. Do NOT guess tool names.");
                        }
                        else if (c.TopItems.Count > 0)
                        {
                            sb.AppendLine($"  Top skills: {string.Join(", ", c.TopItems)}");
                        }
                    }
                    chatMessages.Add(new ChatMessage(ChatRole.System, sb.ToString()));
                    logger.LogInformation(
                        "Completion re-prompt: injected {Count} service hint(s) via enriched search: {Services}",
                        candidates.Count, string.Join(", ", candidates.Select(c => c.Id)));
                    injectedAny = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Completion re-prompt: service search failed (non-fatal)");
            }
        }

        if (!injectedAny)
        {
            logger.LogInformation(
                "Completion re-prompt: no additional skills or services found for enriched query");
        }
    }

    /// <summary>
    /// Strips &lt;think&gt; blocks and extracts the first JSON object from raw LLM output.
    /// </summary>
    private static string ExtractJsonObject(string text)
    {
        // Strip <think>...</think> blocks first (DeepSeek reasoning preamble)
        var thinkStart = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        var thinkEnd = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkStart >= 0 && thinkEnd > thinkStart)
            text = text[(thinkEnd + "</think>".Length)..].TrimStart();

        var objStart = text.IndexOf('{');
        var objEnd = text.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart)
            return text[objStart..(objEnd + 1)];

        return string.Empty;
    }
}
