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
/// Its inner client is wrapped in <see cref="TypedToolSurfaceChatClient"/>, so typed tools a
/// session activates mid-loop are offered, and callable, on the loop's next iteration, and in
/// <see cref="ContextNoteChatClient"/>, so stash-registry notes join each request append-only.
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
    private readonly IncompleteReplyCheck _incompleteCheck;

    private int? _knownContextLimit;

    /// <summary>
    /// The tool-iteration budget of a call that sets no <see cref="MaxIterationsOverrideContext"/>.
    /// The base class's <c>MaximumIterationsPerRequest</c> is only a ceiling (set once to
    /// <see cref="int.MaxValue"/>); each call enforces its own budget (#757).
    /// </summary>
    private readonly int _defaultMaxIterations;

    /// <summary>
    /// The loop-tracking state of the <see cref="GetResponseAsync"/> call in progress on this
    /// async flow. This client is a per-tier singleton (<see cref="TieredChatClientRegistry"/>)
    /// that concurrent runs share — the primary agent's sessions and subagents, or several
    /// research runs at once — so the state can't live in instance fields: one run's reset,
    /// timeout or iteration budget would leak into another's, breaking stuck-loop detection,
    /// nudging a healthy run, or cutting a run short. Shared with the
    /// <see cref="IterationBudgetChatClient"/> below this client.
    /// </summary>
    private readonly AsyncLocal<CallState?> _callState;

    /// <summary>
    /// Per-call loop tracking: consecutive tool timeouts, repeated identical calls, and the
    /// call's tool-iteration budget.
    /// </summary>
    private sealed class CallState(int maxIterations)
    {
        public int ConsecutiveTimeoutIterations;
        public AgentLoopRunner.RepetitiveToolCallDetector RepetitiveCallDetector { get; } = new();

        /// <summary>Tool rounds this call may run before its final request, which offers no tools.</summary>
        public int MaxIterations { get; } = maxIterations;

        /// <summary>
        /// Model requests this call's tool loop has sent, counted by
        /// <see cref="IterationBudgetChatClient"/>. FICC sends one request, then one more after
        /// each tool round, whether or not the round's functions resolved. So request
        /// <c>n</c> offers tools only while <c>n &lt;= MaxIterations</c>.
        /// </summary>
        public int ModelRequests;

        private volatile bool _loopEnded;

        /// <summary>
        /// Set once the tool loop has returned, so the summary follow-up isn't counted against
        /// the budget.
        /// </summary>
        public bool LoopEnded
        {
            get => _loopEnded;
            set => _loopEnded = value;
        }
    }

    /// <summary>
    /// The current call's state. Every call sets one (<see cref="GetResponseAsync"/> and
    /// <see cref="GetStreamingResponseAsync"/>); the fallback, a fresh state per tool invocation,
    /// tracks nothing across calls, which is the safe default.
    /// </summary>
    private CallState CurrentCallState => _callState.Value ?? NewCallState();

    /// <summary>
    /// A fresh state for a call starting on this async flow. Its budget is the per-request
    /// override (e.g. a subagent spawned with an elevated budget) or the tier default.
    /// </summary>
    private CallState NewCallState() =>
        new(Math.Max(1, MaxIterationsOverrideContext.Value ?? _defaultMaxIterations));

    public RockBotFunctionInvokingChatClient(
        IChatClient innerClient,
        IToolProgressNotifier? progressNotifier,
        IToolCallLog? toolCallLog,
        ModelBehavior modelBehavior,
        LlmCostEstimator costEstimator,
        IWorkingMemory workingMemory,
        IOptions<AgentHostOptions> hostOptions,
        ILogger logger,
        IncompleteReplyCheck? incompleteCheck = null) : base(WrapInner(innerClient, logger, out var callState))
    {
        _callState = callState;
        _incompleteCheck = incompleteCheck ?? new IncompleteReplyCheck();
        _progressNotifier = progressNotifier;
        _toolCallLog = toolCallLog;
        _modelBehavior = modelBehavior;
        _costEstimator = costEstimator;
        _workingMemory = workingMemory;
        _hostOptions = hostOptions;
        _logger = logger;

        _defaultMaxIterations = Math.Max(1,
            modelBehavior.MaxToolIterationsOverride ?? hostOptions.Value.MaxToolIterations);

        // Only a ceiling, never written again: concurrent calls on this shared instance each
        // enforce their own budget (CallState.MaxIterations) in IterationBudgetChatClient and
        // InvokeFunctionAsync. Callers may ask for any budget (a subagent's max_iterations),
        // so there is no fixed highest value to use instead (#757).
        MaximumIterationsPerRequest = int.MaxValue;
    }

    /// <summary>
    /// Builds the chain below FICC: the iteration-budget gate (where FICC's own final,
    /// tool-less request would enter), then the typed-tool surface and context notes. The
    /// gate shares the call-state slot with this client.
    /// </summary>
    private static IChatClient WrapInner(
        IChatClient innerClient, ILogger logger, out AsyncLocal<CallState?> callState)
    {
        callState = new AsyncLocal<CallState?>();
        return new IterationBudgetChatClient(
            new TypedToolSurfaceChatClient(new ContextNoteChatClient(innerClient)), callState, logger);
    }

    /// <summary>
    /// Gives a streaming call its own state too, so its budget is enforced the same way. An
    /// async iterator resumes on its consumer's execution context after each <c>yield</c>, so
    /// the state is set again before each step of the inner loop.
    /// </summary>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var callState = NewCallState();
        _callState.Value = callState;
        await using var updates = base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            _callState.Value = callState;
            if (!await updates.MoveNextAsync())
                yield break;
            yield return updates.Current;
        }
    }

    protected override async ValueTask<object?> InvokeFunctionAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        var callContent = context.CallContent;

        // #757: the per-call iteration budget. FunctionInvocationContext.Iteration is 0-based.
        // IterationBudgetChatClient counts the rounds and strips tools from the final request;
        // this catches a call the model makes anyway in answer to it.
        var budget = CurrentCallState.MaxIterations;
        if (context.Iteration >= budget)
        {
            // FICC never runs such calls; end the loop without running, recording or announcing
            // this one.
            context.Terminate = true;
            _logger.LogWarning(
                "Not running tool {Name}(callId={CallId}): iteration budget of {Budget} reached",
                callContent.Name, callContent.CallId, budget);
            return $"Not run: the tool-iteration budget for this request ({budget}) has been reached.";
        }

        var argsSummary = callContent.Arguments is { Count: > 0 }
            ? string.Join(", ", callContent.Arguments.Select(a => $"{a.Key}={a.Value}"))
            : null;

        _logger.LogInformation("Executing tool {Name}(callId={CallId}, args={Args})",
            callContent.Name, callContent.CallId, AgentLoopRunner.FormatArgsForLog(callContent.Arguments));

        // #685: an external change the user did not ask for is refused, not run. The refusal is
        // the tool result, recorded as a failed call so the evaluator and relays don't count it.
        if (await ActionGateContext.CheckAsync(callContent.Name, argsSummary, cancellationToken) is { } refusal)
        {
            ToolDiagnostics.Invocations.Add(1,
                new KeyValuePair<string, object?>("rockbot.tool.name", callContent.Name),
                new KeyValuePair<string, object?>("rockbot.tool.status", AgentLoopRunner.ConsequentialActionGatedStatus));
            if (LoopDiagnosticsContext.Value is { } diagGated)
            {
                diagGated.ToolCalls++;
                diagGated.LastToolName = callContent.Name;
                diagGated.LastToolStatus = AgentLoopRunner.ConsequentialActionGatedStatus;
                diagGated.LastToolResult = refusal;
            }
            LoopToolCallLedgerContext.Value?.Record(
                callContent.Name, AgentLoopRunner.TruncateLedgerArgs(argsSummary), succeeded: false,
                rawArguments: callContent.Arguments);
            return refusal;
        }

        // Temporary diagnostic — per-tool-call context size for patrol/* sessions so we
        // can see growth across FICC's internal iterations (this override is the only
        // observable boundary inside FunctionInvokingChatClient's tool loop).
        if (context.Messages is IList<ChatMessage> ficcMessages)
        {
            AgentLoopRunner.LogContextSize(
                ficcMessages,
                $"ficc-pre-{callContent.Name}",
                ToolCallSessionContext.SessionId,
                _logger,
                _hostOptions.Value.ImageCost);
            // Per-LLM-call histogram. Pre-invoke approximates the size that produced the
            // immediately-prior LLM response (minus that response's own assistant turn) —
            // a faithful proxy for "context size at each internal FICC iteration" without
            // having to override base.GetResponseAsync.
            AgentLoopRunner.RecordLlmCallContextSize(
                ficcMessages,
                ToolCallSessionContext.SessionId,
                _hostOptions.Value.ImageCost,
                _logger);
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
        // Any stash entries it adds are rendered by ContextNoteChatClient before the next
        // request: a message added here would split this assistant turn from its results.
        if (context.Messages is List<ChatMessage> trimList
            && WatermarkTokens is int watermarkTokens
            and > 0)
        {
            int effectiveLimit = _knownContextLimit is int hardLimit
                ? Math.Min(watermarkTokens, hardLimit)
                : watermarkTokens;
            await TrimLargeToolResultsAsync(trimList, effectiveLimit);
        }

        using var activity = ToolDiagnostics.Source.StartActivity(
            $"tool.invoke {callContent.Name}", ActivityKind.Internal);
        activity?.SetTag("rockbot.tool.name", callContent.Name);
        activity?.SetTag("rockbot.tool.call_id", callContent.CallId);

        Activity.Current?.AddEvent(new ActivityEvent("tool_selection_made",
            tags: new ActivityTagsCollection { { "tool", callContent.Name } }));

        // #795: one id ties the call's started and finished/failed reports together, and names the
        // call that spawned any subagent or worker it starts.
        var toolCallId = string.IsNullOrEmpty(callContent.CallId) ? Guid.NewGuid().ToString("N") : callContent.CallId;
        if (_progressNotifier is not null)
        {
            var desc = DescribeToolCall(callContent.Name, argsSummary);
            await _progressNotifier.OnToolCallAsync(new UserProxy.ToolCallInfo
            {
                ToolCallId = toolCallId,
                ToolCallName = callContent.Name,
                Phase = UserProxy.ToolCallPhase.Started,
                ArgsSummary = desc,
            }, cancellationToken);
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

        // Mid-turn tier escalation (#663): a side-effecting call on a Low run moves the
        // loop's remaining iterations to Balanced (see TierEscalatingChatClient).
        TierEscalationContext.Value?.ObserveToolCall(callContent.Name, callContent.Arguments);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var status = "ok";
        object? result;
        // #686: a batch tool reports failures of the work it ran (and the calls it made) here,
        // since its result is deliberately not an error.
        var outcome = new ToolCallOutcome();
        using var outcomeScope = ToolCallOutcomeContext.Set(outcome);
        try
        {
            using var callIdScope = CurrentToolCallContext.Set(toolCallId);
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
            TierEscalationContext.Value?.ObserveToolResult(callContent.Name, isError: true);
            LoopToolCallLedgerContext.Value?.Record(
                callContent.Name, AgentLoopRunner.TruncateLedgerArgs(argsSummary), succeeded: false,
                rawArguments: callContent.Arguments, outcome: outcome);
            if (_progressNotifier is not null)
            {
                await _progressNotifier.OnToolCallAsync(new UserProxy.ToolCallInfo
                {
                    ToolCallId = toolCallId,
                    ToolCallName = callContent.Name,
                    Phase = UserProxy.ToolCallPhase.Failed,
                    DurationMs = sw.ElapsedMilliseconds,
                    Error = ex.Message,
                }, cancellationToken);
            }
            throw;
        }
        sw.Stop();

        var resultStr = result?.ToString();
        _logger.LogInformation("Tool {Name} returned in {ElapsedMs}ms: {Result}",
            callContent.Name, sw.ElapsedMilliseconds,
            resultStr is { Length: > 200 } ? resultStr[..200] + "..." : resultStr);

        // Track consecutive timeouts
        var callState = CurrentCallState;
        if (resultStr is not null && ToolResultClassifier.IsTimeout(resultStr))
        {
            status = ToolError.Codes.Timeout;
            var timeouts = Interlocked.Increment(ref callState.ConsecutiveTimeoutIterations);
            if (timeouts >= MaxConsecutiveTimeoutIterations)
            {
                _logger.LogWarning(
                    "Aborting: {N} consecutive iterations with tool timeouts",
                    timeouts);
            }
        }
        else
        {
            Volatile.Write(ref callState.ConsecutiveTimeoutIterations, 0);
        }

        // Detect content-level errors that didn't throw an exception. Tool executors that
        // return ToolInvokeResponse with IsError=true are surfaced by RegistryToolFunction
        // as a string prefixed with "Error: " — so any tool result starting with that prefix
        // indicates a logical failure even though the call mechanically completed. Flagging
        // these as Succeeded=false is what lets the dream-time retry-pattern detector see
        // MCP errors (e.g. "the resource could not be found") as failures rather than as
        // successful calls that just happened to return an error message.
        if (status == "ok" && resultStr is not null && ToolResultClassifier.IsError(resultStr))
        {
            status = ToolError.Codes.ExecutionFailed;
        }

        // #686: a batch tool whose result is not an error but whose work partly failed (spawn_wisps
        // with aborted wisps) is a failed call for the ledger, the log and the evaluator. The
        // result the model sees is unchanged, so it does not re-run the parts that worked.
        if (status == "ok" && outcome.Succeeded == false)
        {
            status = ToolError.Codes.ExecutionFailed;
            _logger.LogInformation("Tool {Name} reported a failed outcome: {Detail}",
                callContent.Name, outcome.Detail ?? "(no detail)");
        }

        TierEscalationContext.Value?.ObserveToolResult(callContent.Name, isError: status != "ok");

        // Track consecutive identical (tool, args, result) triples to detect stuck loops.
        if (callState.RepetitiveCallDetector.Track(callContent.Name, argsSummary ?? string.Empty, resultStr ?? string.Empty))
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
            // The new stash key reaches the model through ContextNoteChatClient, which adds a
            // registry note to the next request.
            if (!ReferenceEquals(capped, resultStr))
                result = capped;
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
        LoopToolCallLedgerContext.Value?.Record(
            callContent.Name, AgentLoopRunner.TruncateLedgerArgs(argsSummary), succeeded: status == "ok",
            rawArguments: callContent.Arguments, outcome: outcome);

        // Log tool-call event for sequence analysis (fire-and-forget)
        if (_toolCallLog is not null && ToolCallSessionContext.SessionId is { } sid)
        {
            _ = _toolCallLog.AppendAsync(new ToolCallEvent(
                SessionId: sid,
                ToolName: callContent.Name,
                ArgumentsSummary: argsSummary,
                Succeeded: status == "ok",
                DurationMs: (int)sw.ElapsedMilliseconds,
                Timestamp: DateTimeOffset.UtcNow)
            {
                ErrorMessage = outcome.Succeeded == false ? outcome.Detail : null,
                Detail = outcome.Detail,
                NestedCalls = ToolCallOutcome.FormatNested(outcome.NestedSnapshot()),
            });
        }

        if (_progressNotifier is not null)
        {
            var summary = resultStr is { Length: > 100 } ? resultStr[..100] + "..." : resultStr;
            var failed = status != "ok";
            await _progressNotifier.OnToolCallAsync(new UserProxy.ToolCallInfo
            {
                ToolCallId = toolCallId,
                ToolCallName = callContent.Name,
                Phase = failed ? UserProxy.ToolCallPhase.Failed : UserProxy.ToolCallPhase.Finished,
                Result = summary,
                DurationMs = sw.ElapsedMilliseconds,
                Error = failed ? (outcome.Succeeded == false ? outcome.Detail : null) ?? summary : null,
            }, cancellationToken);
        }

        return result;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // A fresh state for this call. Setting an AsyncLocal inside an async method is scoped to
        // this call's flow: the tool invocations below see it, and a concurrent call on the same
        // instance (another run on this tier) keeps its own.
        // Its iteration budget lives in that state; the shared instance is never changed (#757).
        var callState = NewCallState();
        _callState.Value = callState;

        _logger.LogInformation(
            "RockBotFunctionInvokingChatClient handling request (maxIterations={MaxIter})",
            callState.MaxIterations);
        var messageList = messages as List<ChatMessage> ?? [.. messages];

        // Soft watermark: trim proactively when the message list exceeds
        // ToolResultStashWatermarkTokens, without waiting for a provider 400.
        // Falls back to _knownContextLimit when the watermark is disabled or larger
        // than the learned hard limit. (issue: context-bloat investigation)
        var watermark = WatermarkTokens;
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
            when (ex.Status == 400 && ContextOverflowParser.TryParse(ex.Message, out var max, out var used))
        {
            _knownContextLimit = max;
            _logger.LogWarning(
                "Context overflow ({Used:N0}/{Max:N0} tokens); trimming tool results and retrying once",
                used, max);
            await TrimLargeToolResultsAsync(messageList, max);
            // FICC restarts its iteration count for the retry, so the retry gets a full budget.
            Volatile.Write(ref callState.ModelRequests, 0);
            response = await base.GetResponseAsync(messageList, options, cancellationToken);
        }
        callState.LoopEnded = true;

        // If the response looks like max-iterations was hit (incomplete setup phrase),
        // make one follow-up call asking for a backward-looking summary.
        var text = ExtractAssistantText(response);
        // Empty is deterministic; a trailing ":" or "..." is only a candidate, confirmed by the
        // decision model before the reply is replaced with a summary.
        if (string.IsNullOrWhiteSpace(text) || await _incompleteCheck.IsIncompleteAsync(text, cancellationToken))
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
                    var costUsd = _costEstimator.EstimateCost(summaryModelId, summaryUsage);
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

    /// <summary>
    /// Trims oversized tool results to a head+tail surface with an elision marker, and
    /// stashes the full original in working memory under <c>stash/{sessionId}/{callId}</c>
    /// (via the per-run <see cref="AgentLoopStashContext"/>) so the model can recover it
    /// via <c>GetFromWorkingMemory</c> using a key surfaced in the system stash registry.
    /// Falls back to the legacy head-only behaviour when no stash context is bound or
    /// the tool result has no callId.
    /// </summary>
    private Task TrimLargeToolResultsAsync(List<ChatMessage> messages, int maxTokens)
    {
        var stashState = AgentLoopStashContext.Value;
        return ToolResultTrimmer.TrimAsync(
            messages, maxTokens, stashState?.SessionId, stashState, _workingMemory,
            _hostOptions.Value.ToolResultStashHeadTailRatio,
            _hostOptions.Value.ToolResultStashTtlMinutes,
            _logger,
            _hostOptions.Value.ImageCost,
            _hostOptions.Value.ToolResultStashTrimTargetRatio,
            _hostOptions.Value.ToolResultTrimFloorChars);
    }

    /// <summary>
    /// The watermark for the run in progress: the tier-resolved value
    /// <see cref="AgentLoopRunner"/> put on the stash state, else the global option.
    /// </summary>
    private int WatermarkTokens =>
        AgentLoopStashContext.Value?.WatermarkTokens ?? _hostOptions.Value.ToolResultStashWatermarkTokens;

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

    private static string DescribeToolCall(string name, string? argsSummary)
    {
        if (string.IsNullOrEmpty(argsSummary)) return name;
        return $"{name}({argsSummary})";
    }

    /// <summary>
    /// Sits directly below FICC and enforces the current call's iteration budget at the one
    /// boundary every round crosses, whether or not the round's functions resolved (an unknown
    /// function name never reaches <see cref="InvokeFunctionAsync"/>). The base class can't do
    /// this per call, since its limit is the shared instance's (#757).
    /// <list type="bullet">
    /// <item>Request <c>MaxIterations + 1</c> is FICC's final request at the limit: it goes out with
    /// no <c>Tools</c> and no <c>ToolMode</c>, so the model has to answer.</item>
    /// <item>A later request means the model answered that one with calls anyway and none of them
    /// resolved (a known one ends the loop in <see cref="InvokeFunctionAsync"/>). The model isn't
    /// asked again: an empty reply ends the loop.</item>
    /// </list>
    /// </summary>
    private sealed class IterationBudgetChatClient(
        IChatClient innerClient, AsyncLocal<CallState?> callState, ILogger logger)
        : DelegatingChatClient(innerClient)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Gate(ref options)
                ? base.GetResponseAsync(messages, options, cancellationToken)
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [])));

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Gate(ref options)
                ? base.GetStreamingResponseAsync(messages, options, cancellationToken)
                : AsyncEnumerable.Empty<ChatResponseUpdate>();

        /// <summary>
        /// Counts this request against the call's budget and strips tools from the final one.
        /// Returns false when the loop is past its final request and must end.
        /// </summary>
        private bool Gate(ref ChatOptions? options)
        {
            if (callState.Value is not { LoopEnded: false } state)
                return true;

            var request = Interlocked.Increment(ref state.ModelRequests);
            if (request > state.MaxIterations + 1)
            {
                logger.LogWarning(
                    "Ending tool loop: the model kept calling tools that could not be resolved after " +
                    "its final request (iteration budget {Budget})", state.MaxIterations);
                return false;
            }

            if (request > state.MaxIterations && options is not null)
            {
                options = options.Clone();
                options.Tools = null;
                options.ToolMode = null;
            }

            return true;
        }
    }
}
