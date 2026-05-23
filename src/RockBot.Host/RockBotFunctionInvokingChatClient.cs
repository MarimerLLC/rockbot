using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Llm;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// Subclass of <see cref="FunctionInvokingChatClient"/> that preserves the infrastructure
/// <see cref="AgentLoopRunner"/> previously provided for native tool-calling models:
/// progress notifications, consecutive timeout detection, and context overflow recovery.
/// </summary>
public class RockBotFunctionInvokingChatClient : FunctionInvokingChatClient
{
    private const int MaxConsecutiveTimeoutIterations = 2;

    private readonly IToolProgressNotifier? _progressNotifier;
    private readonly IToolCallLog? _toolCallLog;
    private readonly ModelBehavior _modelBehavior;
    private readonly LlmCostEstimator _costEstimator;
    private readonly IWorkingMemory _workingMemory;
    private readonly IOptions<AgentHostOptions> _hostOptions;
    private readonly ILogger _logger;

    private int _consecutiveTimeoutIterations;
    private int? _knownContextLimit;
    private readonly AgentLoopRunner.RepetitiveToolCallDetector _repetitiveCallDetector = new();

    public RockBotFunctionInvokingChatClient(
        IChatClient innerClient,
        IToolProgressNotifier? progressNotifier,
        IToolCallLog? toolCallLog,
        ModelBehavior modelBehavior,
        LlmCostEstimator costEstimator,
        IWorkingMemory workingMemory,
        IOptions<AgentHostOptions> hostOptions,
        ILogger logger) : base(innerClient)
    {
        _progressNotifier = progressNotifier;
        _toolCallLog = toolCallLog;
        _modelBehavior = modelBehavior;
        _costEstimator = costEstimator;
        _workingMemory = workingMemory;
        _hostOptions = hostOptions;
        _logger = logger;

        MaximumIterationsPerRequest = modelBehavior.MaxToolIterationsOverride ?? hostOptions.Value.MaxToolIterations;
    }

    protected override async ValueTask<object?> InvokeFunctionAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        var callContent = context.CallContent;
        var argsSummary = callContent.Arguments is { Count: > 0 }
            ? string.Join(", ", callContent.Arguments.Select(a => $"{a.Key}={a.Value}"))
            : null;

        _logger.LogInformation("Executing tool {Name}(callId={CallId}, args={Args})",
            callContent.Name, callContent.CallId, argsSummary ?? "(none)");

        // Temporary diagnostic — per-tool-call context size for patrol/* sessions so we
        // can see growth across FICC's internal iterations (this override is the only
        // observable boundary inside FunctionInvokingChatClient's tool loop).
        if (context.Messages is IList<ChatMessage> ficcMessages)
        {
            AgentLoopRunner.LogContextSize(
                ficcMessages,
                $"ficc-pre-{callContent.Name}",
                ToolCallSessionContext.SessionId,
                _logger);
            // Per-LLM-call histogram. Pre-invoke approximates the size that produced the
            // immediately-prior LLM response (minus that response's own assistant turn) —
            // a faithful proxy for "context size at each internal FICC iteration" without
            // having to override base.GetResponseAsync.
            AgentLoopRunner.RecordLlmCallContextSize(
                ficcMessages,
                ToolCallSessionContext.SessionId);
        }

        // Age out BM25-recalled skill bodies the model hasn't referenced in N tool
        // calls. Runs before the watermark trim so it has the chance to shrink the
        // message list enough to keep the watermark from firing at all.
        if (context.Messages is IList<ChatMessage> skillAgingList
            && LoadedSkillsContext.Value is { } loadedSkillsState
            && _hostOptions.Value.SkillBodyUnloadAfterIterations is int unloadAfter
            and > 0)
        {
            AgentLoopRunner.RegisterAndAgeSkillBodies(
                skillAgingList,
                loadedSkillsState,
                callContent.Name,
                callContent.Arguments,
                unloadAfter,
                _logger);
        }

        // Soft watermark trim inside FICC's inner loop. Without this, long tool-heavy
        // subagent runs grow the message list from ~17k to 100k+ tokens entirely within
        // a single outer GetResponseAsync call, and the outer-boundary trim never fires.
        // The 'pre-invoke' point is the right hook because (N-1) prior tool results have
        // already been appended to context.Messages by this point — we trim the tail
        // accumulated so far before letting FICC append yet another large result.
        if (context.Messages is List<ChatMessage> trimList
            && _hostOptions.Value.ToolResultStashWatermarkTokens is int watermarkTokens
            and > 0)
        {
            int effectiveLimit = _knownContextLimit is int hardLimit
                ? Math.Min(watermarkTokens, hardLimit)
                : watermarkTokens;
            await TrimLargeToolResultsAsync(trimList, effectiveLimit);
            if (AgentLoopStashContext.Value is { } stashStateInner)
                AgentLoopRunner.RefreshStashRegistryContext(trimList, stashStateInner.Registry);
        }

        using var activity = ToolDiagnostics.Source.StartActivity(
            $"tool.invoke {callContent.Name}", ActivityKind.Internal);
        activity?.SetTag("rockbot.tool.name", callContent.Name);
        activity?.SetTag("rockbot.tool.call_id", callContent.CallId);

        Activity.Current?.AddEvent(new ActivityEvent("tool_selection_made",
            tags: new ActivityTagsCollection { { "tool", callContent.Name } }));

        if (_progressNotifier is not null)
        {
            var desc = DescribeToolCall(callContent.Name, argsSummary);
            await _progressNotifier.OnToolInvokingAsync(callContent.Name, desc, cancellationToken);
        }

        // Record args summary in the per-run stash registry context so that if this
        // tool result is later overflow-trimmed, the registry entry can include a
        // meaningful description of the call. Issue #337.
        if (AgentLoopStashContext.Value is { } stashCapture && !string.IsNullOrEmpty(callContent.CallId))
        {
            stashCapture.ArgsSummaries[callContent.CallId] =
                AgentLoopRunner.TruncateArgsSummary(argsSummary ?? "(none)");
        }

        // Record the in-flight tool in LoopDiagnostics so a mid-call cancel still
        // leaves a usable "last tool" trail for the caller's failure handler.
        if (LoopDiagnosticsContext.Value is { } diagPre)
        {
            diagPre.ToolCalls++;
            diagPre.LastToolName = callContent.Name;
            diagPre.LastToolArguments = argsSummary is { Length: > 500 }
                ? argsSummary[..500] + "…"
                : argsSummary;
            diagPre.LastToolStartedAt = DateTimeOffset.UtcNow;
            diagPre.LastToolCompletedAt = null;
            diagPre.LastToolResult = null;
            diagPre.LastToolStatus = "in-flight";
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var status = "ok";
        object? result;
        try
        {
            result = await base.InvokeFunctionAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Record cancel before unwinding so the failure handler sees this tool as the last one.
            if (LoopDiagnosticsContext.Value is { } diagCancel)
            {
                diagCancel.LastToolCompletedAt = DateTimeOffset.UtcNow;
                diagCancel.LastToolStatus = "cancelled";
            }
            throw; // host shutting down — don't record metrics
        }
        catch (Exception ex)
        {
            sw.Stop();
            status = ex switch
            {
                TimeoutException => ToolError.Codes.Timeout,
                ArgumentException => ToolError.Codes.InvalidArguments,
                _ => ToolError.Codes.ExecutionFailed
            };
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("rockbot.tool.error_code", status);
            ToolDiagnostics.InvokeDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("rockbot.tool.name", callContent.Name),
                new KeyValuePair<string, object?>("rockbot.tool.status", status));
            ToolDiagnostics.Invocations.Add(1,
                new KeyValuePair<string, object?>("rockbot.tool.name", callContent.Name),
                new KeyValuePair<string, object?>("rockbot.tool.status", status));

            if (LoopDiagnosticsContext.Value is { } diagEx)
            {
                diagEx.LastToolCompletedAt = DateTimeOffset.UtcNow;
                diagEx.LastToolStatus = status;
                diagEx.LastToolResult = $"Error: {ex.Message}";
            }
            throw;
        }
        sw.Stop();

        var resultStr = result?.ToString();
        _logger.LogInformation("Tool {Name} returned in {ElapsedMs}ms: {Result}",
            callContent.Name, sw.ElapsedMilliseconds,
            resultStr is { Length: > 200 } ? resultStr[..200] + "..." : resultStr);

        // Track consecutive timeouts
        if (resultStr is not null && IsTimeoutResult(resultStr))
        {
            status = ToolError.Codes.Timeout;
            _consecutiveTimeoutIterations++;
            if (_consecutiveTimeoutIterations >= MaxConsecutiveTimeoutIterations)
            {
                _logger.LogWarning(
                    "Aborting: {N} consecutive iterations with tool timeouts",
                    _consecutiveTimeoutIterations);
            }
        }
        else
        {
            _consecutiveTimeoutIterations = 0;
        }

        // Detect content-level errors that didn't throw an exception. Tool executors that
        // return ToolInvokeResponse with IsError=true are surfaced by RegistryToolFunction
        // as a string prefixed with "Error: " — so any tool result starting with that prefix
        // indicates a logical failure even though the call mechanically completed. Flagging
        // these as Succeeded=false is what lets the dream-time retry-pattern detector see
        // MCP errors (e.g. "the resource could not be found") as failures rather than as
        // successful calls that just happened to return an error message.
        if (status == "ok" && resultStr is not null && IsErrorResult(resultStr))
        {
            status = ToolError.Codes.ExecutionFailed;
        }

        // Track consecutive identical (tool, args, result) triples to detect stuck loops.
        if (_repetitiveCallDetector.Track(callContent.Name, argsSummary ?? string.Empty, resultStr ?? string.Empty))
        {
            _logger.LogWarning(
                "Detected {Threshold} consecutive identical tool call results for {Name}; " +
                "appending loop-break nudge to result",
                AgentLoopRunner.RepetitiveToolCallDetector.Threshold, callContent.Name);
            var nudge =
                $"\n\n[System note: You have called {callContent.Name} with the same arguments " +
                $"{AgentLoopRunner.RepetitiveToolCallDetector.Threshold} times and received the same " +
                "result each time. Please try a different approach.]";
            result = (resultStr ?? string.Empty) + nudge;
            resultStr = (string?)result;
        }

        // Per-tool-result cap: if this single result is larger than ToolResultMaxChars,
        // stash the original and replace inline with a head + elision marker + tail.
        // The watermark trim (TrimLargeToolResultsAsync) only fires when total context
        // crosses ~144k chars, which lets a single 36k-char schema dump bloat the loop
        // unchecked. This per-call cap catches that case at the source.
        var maxResultChars = _hostOptions.Value.ToolResultMaxChars;
        if (maxResultChars > 0 && resultStr is { Length: > 0 } && resultStr.Length > maxResultChars)
        {
            var ttl = TimeSpan.FromMinutes(Math.Max(1, _hostOptions.Value.ToolResultStashTtlMinutes));
            var capped = await AgentLoopRunner.CapToolResultAsync(
                resultStr,
                callContent.CallId,
                callContent.Name,
                _workingMemory,
                AgentLoopStashContext.Value,
                maxResultChars,
                _hostOptions.Value.ToolResultStashHeadTailRatio,
                ttl,
                _logger);
            if (!ReferenceEquals(capped, resultStr))
            {
                result = capped;
                // Refresh the stash registry system message so the next LLM round-trip
                // can see (and instruct the model to use) the new stash key. FICC won't
                // re-enter our pre-invoke hook before posting the current tool result
                // back to the model, so this is the right place to do it.
                if (context.Messages is List<ChatMessage> registryMessages
                    && AgentLoopStashContext.Value is { } stashStateForRegistry)
                {
                    AgentLoopRunner.RefreshStashRegistryContext(
                        registryMessages, stashStateForRegistry.Registry);
                }
            }
        }

        ToolDiagnostics.InvokeDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("rockbot.tool.name", callContent.Name),
            new KeyValuePair<string, object?>("rockbot.tool.status", status));
        ToolDiagnostics.Invocations.Add(1,
            new KeyValuePair<string, object?>("rockbot.tool.name", callContent.Name),
            new KeyValuePair<string, object?>("rockbot.tool.status", status));

        if (LoopDiagnosticsContext.Value is { } diagPost)
        {
            diagPost.LastToolCompletedAt = DateTimeOffset.UtcNow;
            diagPost.LastToolStatus = status;
            diagPost.LastToolResult = resultStr is { Length: > 500 }
                ? resultStr[..500] + "…"
                : resultStr;
        }

        // Log tool-call event for sequence analysis (fire-and-forget)
        if (_toolCallLog is not null && ToolCallSessionContext.SessionId is { } sid)
        {
            _ = _toolCallLog.AppendAsync(new ToolCallEvent(
                SessionId: sid,
                ToolName: callContent.Name,
                ArgumentsSummary: argsSummary,
                Succeeded: status == "ok",
                DurationMs: (int)sw.ElapsedMilliseconds,
                Timestamp: DateTimeOffset.UtcNow));
        }

        if (_progressNotifier is not null)
        {
            var summary = resultStr is { Length: > 100 } ? resultStr[..100] + "..." : resultStr;
            await _progressNotifier.OnToolInvokedAsync(callContent.Name, summary, cancellationToken);
        }

        return result;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _consecutiveTimeoutIterations = 0;
        _repetitiveCallDetector.Reset();

        // Apply per-request max iterations override if set (e.g. by a subagent
        // that was spawned with an elevated iteration budget).
        var saved = MaximumIterationsPerRequest;
        if (MaxIterationsOverrideContext.Value is int overrideValue)
            MaximumIterationsPerRequest = overrideValue;

        _logger.LogInformation(
            "RockBotFunctionInvokingChatClient handling request (maxIterations={MaxIter})",
            MaximumIterationsPerRequest);
        var messageList = messages as List<ChatMessage> ?? [.. messages];

        try
        {
        // Soft watermark: trim proactively when the message list exceeds
        // ToolResultStashWatermarkTokens, without waiting for a provider 400.
        // Falls back to _knownContextLimit when the watermark is disabled or larger
        // than the learned hard limit. (issue: context-bloat investigation)
        var watermark = _hostOptions.Value.ToolResultStashWatermarkTokens;
        int? effectiveLimit = (watermark, _knownContextLimit) switch
        {
            (> 0, int hard) => Math.Min(watermark, hard),
            (> 0, null) => watermark,
            (_, int hard) => hard,
            _ => null
        };
        if (effectiveLimit is int preLimit)
            await TrimLargeToolResultsAsync(messageList, preLimit);

        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messageList, options, cancellationToken);
        }
        catch (ClientResultException ex)
            when (ex.Status == 400 && TryParseContextOverflow(ex.Message, out var max, out var used))
        {
            _knownContextLimit = max;
            _logger.LogWarning(
                "Context overflow ({Used:N0}/{Max:N0} tokens); trimming tool results and retrying once",
                used, max);
            await TrimLargeToolResultsAsync(messageList, max);
            response = await base.GetResponseAsync(messageList, options, cancellationToken);
        }

        // If the response looks like max-iterations was hit (incomplete setup phrase),
        // make one follow-up call asking for a backward-looking summary.
        var text = ExtractAssistantText(response);
        if (AgentLoopRunner.IsIncompleteSetupPhrase(text) || string.IsNullOrWhiteSpace(text))
        {
            var hasToolCalls = response.Messages
                .Any(m => m.Contents.OfType<FunctionCallContent>().Any());
            if (hasToolCalls || string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation(
                    "Response looks incomplete after tool loop; requesting backward-looking summary");

                var summaryMessages = new List<ChatMessage>(messageList);
                summaryMessages.AddRange(response.Messages);
                var (orphanedCalls, orphanedResults) = StripOrphanedToolCalls(summaryMessages);
                if (orphanedCalls > 0 || orphanedResults > 0)
                {
                    _logger.LogWarning(
                        "Stripped {Calls} orphaned tool call(s) and {Results} orphaned tool result(s) before summary follow-up",
                        orphanedCalls, orphanedResults);
                }
                summaryMessages.Add(new ChatMessage(ChatRole.User,
                    "The task loop has ended. Write a concise summary of what was accomplished. " +
                    "Report only what was completed — do not describe intentions or future actions."));

                var summaryModelId = InnerClient.GetService<ChatClientMetadata>()?.DefaultModelId ?? string.Empty;
                var summarySw = Stopwatch.StartNew();
                var summaryStatus = "ok";
                ChatResponse summaryResponse = null!;
                try
                {
                    summaryResponse = await InnerClient.GetResponseAsync(
                        summaryMessages, new ChatOptions(), cancellationToken);
                }
                catch (Exception)
                {
                    summaryStatus = "error";
                    throw;
                }
                finally
                {
                    summarySw.Stop();
                    var modelTag = new KeyValuePair<string, object?>("rockbot.llm.model", summaryModelId);
                    HostDiagnostics.LlmRequestDuration.Record(summarySw.Elapsed.TotalMilliseconds,
                        new KeyValuePair<string, object?>("rockbot.llm.status", summaryStatus),
                        modelTag);
                    HostDiagnostics.LlmRequests.Add(1,
                        new KeyValuePair<string, object?>("rockbot.llm.status", summaryStatus),
                        modelTag);
                }

                if (summaryResponse.Usage is { } summaryUsage)
                {
                    var inputTokens = summaryUsage.InputTokenCount ?? 0;
                    var outputTokens = summaryUsage.OutputTokenCount ?? 0;
                    var cachedInputTokens = UsageReader.GetCachedInputTokens(summaryUsage);
                    var modelTag = new KeyValuePair<string, object?>("rockbot.llm.model", summaryModelId);
                    if (summaryUsage.InputTokenCount.HasValue)
                        HostDiagnostics.LlmTokenInput.Add(inputTokens, modelTag);
                    if (summaryUsage.OutputTokenCount.HasValue)
                        HostDiagnostics.LlmTokenOutput.Add(outputTokens, modelTag);
                    if (cachedInputTokens > 0)
                        HostDiagnostics.LlmTokenInputCached.Add(cachedInputTokens, modelTag);
                    var costUsd = _costEstimator.EstimateCost(summaryModelId, inputTokens, outputTokens);
                    if (costUsd > 0)
                    {
                        HostDiagnostics.LlmCostUsd.Add(costUsd, modelTag);
                        HostDiagnostics.LlmCostPerRequest.Record(costUsd, modelTag);
                    }

                    if (inputTokens > 0)
                    {
                        var cachePct = cachedInputTokens * 100.0 / inputTokens;
                        _logger.LogInformation(
                            "Summary-follow-up usage: model={ModelId} input={InputTokens} cached={CachedTokens} ({CachePct:F1}%) output={OutputTokens}",
                            summaryModelId, inputTokens, cachedInputTokens, cachePct, outputTokens);
                    }
                }

                var summaryText = ExtractAssistantText(summaryResponse);
                if (!string.IsNullOrWhiteSpace(summaryText))
                {
                    foreach (var msg in summaryResponse.Messages)
                        response.Messages.Add(msg);
                }
            }
        }

        return response;
        }
        finally
        {
            MaximumIterationsPerRequest = saved;
        }
    }

    /// <summary>
    /// Trims oversized tool results to a head+tail surface with an elision marker, and
    /// stashes the full original in working memory under <c>stash/{sessionId}/{callId}</c>
    /// (via the per-run <see cref="AgentLoopStashContext"/>) so the model can recover it
    /// via <c>GetFromWorkingMemory</c> using a key surfaced in the system stash registry.
    /// Falls back to the legacy head-only behaviour when no stash context is bound or
    /// the tool result has no callId.
    /// </summary>
    private async Task TrimLargeToolResultsAsync(List<ChatMessage> messages, int maxTokens)
    {
        const int CharsPerToken = 4;
        var charBudget = (int)(maxTokens * CharsPerToken * 0.9);
        var stashState = AgentLoopStashContext.Value;
        var headRatio = Math.Clamp(_hostOptions.Value.ToolResultStashHeadTailRatio, 0.0, 1.0);
        var ttl = TimeSpan.FromMinutes(Math.Max(1, _hostOptions.Value.ToolResultStashTtlMinutes));

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

            if (stashState is null || string.IsNullOrEmpty(old.CallId))
            {
                var legacyTarget = Math.Max(200, oldStr.Length - excess - 60);
                var legacyTrimmed = oldStr[..legacyTarget] + "\n[truncated to fit context window]";
                messages[bestMsg].Contents[bestContent] =
                    new FunctionResultContent(old.CallId, legacyTrimmed);
                _logger.LogInformation(
                    "Trimmed tool result (legacy mode): {Before:N0} → {After:N0} chars",
                    bestLen, legacyTrimmed.Length);
                continue;
            }

            var marker = $"[content elided to fit context window — id={old.CallId}]";
            var surfaceBudget = Math.Max(200, oldStr.Length - excess - 60 - marker.Length);
            if (surfaceBudget >= oldStr.Length) surfaceBudget = oldStr.Length - 1;
            var headLen = (int)Math.Round(surfaceBudget * headRatio);
            var tailLen = surfaceBudget - headLen;
            if (headLen < 0) headLen = 0;
            if (tailLen < 0) tailLen = 0;
            if (headLen + tailLen >= oldStr.Length) headLen = Math.Max(0, oldStr.Length - tailLen - 1);

            var head = headLen > 0 ? oldStr[..headLen] : string.Empty;
            var tail = tailLen > 0 ? oldStr[^tailLen..] : string.Empty;
            var trimmed = string.Concat(head, "\n\n", marker, "\n\n", tail);

            if (!stashState.Registry.Contains(old.CallId))
            {
                var stashKey = AgentLoopRunner.BuildStashKey(stashState.SessionId, old.CallId);
                try
                {
                    await _workingMemory.SetAsync(
                        stashKey, oldStr, ttl,
                        category: "tool-result-stash",
                        tags: ["stash", "tool-result"]);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to stash original tool result for call {CallId}; trimming without stash",
                        old.CallId);
                }

                stashState.ArgsSummaries.TryGetValue(old.CallId, out var argsSummary);
                stashState.Registry.Add(new ToolResultStashRegistry.Entry(
                    CallId: old.CallId,
                    ToolName: ExtractToolNameForCallId(messages, old.CallId),
                    ArgsSummary: argsSummary ?? "(args unavailable)",
                    Key: stashKey));
            }

            messages[bestMsg].Contents[bestContent] = new FunctionResultContent(old.CallId, trimmed);

            _logger.LogInformation(
                "Trimmed tool result for call {CallId}: {Before:N0} → {After:N0} chars (head {Head}, tail {Tail})",
                old.CallId, bestLen, trimmed.Length, headLen, tailLen);
        }
    }

    private static string ExtractToolNameForCallId(List<ChatMessage> messages, string callId)
    {
        foreach (var msg in messages)
        {
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fcc &&
                    string.Equals(fcc.CallId, callId, StringComparison.Ordinal))
                {
                    return fcc.Name;
                }
            }
        }
        return "(unknown)";
    }

    /// <summary>
    /// Removes <see cref="FunctionCallContent"/> entries whose CallId has no matching
    /// <see cref="FunctionResultContent"/>, and vice versa. The OpenAI-compatible API
    /// rejects requests where an assistant tool_calls message lacks a corresponding tool
    /// response (or where a tool message references an unknown call_id). This pairing
    /// can break when the tool loop hits its iteration limit mid-call, leaving an
    /// unanswered FunctionCallContent in the response we then reuse for a follow-up.
    /// Returns the number of orphans removed in each direction.
    /// </summary>
    internal static (int orphanedCalls, int orphanedResults) StripOrphanedToolCalls(
        List<ChatMessage> messages)
    {
        var resultIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var msg in messages)
            foreach (var c in msg.Contents)
                if (c is FunctionResultContent frc && !string.IsNullOrEmpty(frc.CallId))
                    resultIds.Add(frc.CallId);

        var orphanedCalls = 0;
        foreach (var msg in messages)
        {
            for (var i = msg.Contents.Count - 1; i >= 0; i--)
            {
                if (msg.Contents[i] is FunctionCallContent fcc &&
                    !string.IsNullOrEmpty(fcc.CallId) &&
                    !resultIds.Contains(fcc.CallId))
                {
                    msg.Contents.RemoveAt(i);
                    orphanedCalls++;
                }
            }
        }

        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var msg in messages)
            foreach (var c in msg.Contents)
                if (c is FunctionCallContent fcc && !string.IsNullOrEmpty(fcc.CallId))
                    callIds.Add(fcc.CallId);

        var orphanedResults = 0;
        foreach (var msg in messages)
        {
            for (var i = msg.Contents.Count - 1; i >= 0; i--)
            {
                if (msg.Contents[i] is FunctionResultContent frc &&
                    (string.IsNullOrEmpty(frc.CallId) || !callIds.Contains(frc.CallId)))
                {
                    msg.Contents.RemoveAt(i);
                    orphanedResults++;
                }
            }
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Contents.Count == 0)
                messages.RemoveAt(i);
        }

        return (orphanedCalls, orphanedResults);
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

    private static string ExtractAssistantText(ChatResponse response)
    {
        for (var i = response.Messages.Count - 1; i >= 0; i--)
        {
            var msg = response.Messages[i];
            if (msg.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(msg.Text))
                return msg.Text.Trim();
        }

        return response.Text?.Trim() ?? string.Empty;
    }

    private static bool IsTimeoutResult(string result) =>
        result.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the tool result indicates a logical (content-level) error rather than a
    /// transport success. <see cref="RegistryToolFunction"/> prepends "Error: " to any
    /// response with <c>IsError=true</c>, so this prefix reliably signals "the executor
    /// completed but the operation failed" — including MCP server errors propagated by
    /// <c>McpToolProxy</c>.
    /// </summary>
    internal static bool IsErrorResult(string result) =>
        result.StartsWith("Error: ", StringComparison.Ordinal);

    private static string DescribeToolCall(string name, string? argsSummary)
    {
        if (string.IsNullOrEmpty(argsSummary)) return name;
        return $"{name}({argsSummary})";
    }
}
