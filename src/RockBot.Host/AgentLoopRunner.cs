using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// Reusable LLM tool-calling loop shared by UserMessageHandler, ScheduledTaskHandler,
/// SubagentRunner, and subagent update handlers.
/// </summary>
public sealed class AgentLoopRunner(
    ILlmClient llmClient,
    IWorkingMemory workingMemory,
    ModelBehavior modelBehavior,
    IFeedbackStore feedbackStore,
    ILogger<AgentLoopRunner> logger)
{
    private const int MaxToolIterations = 12;
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
    /// Context window limit in tokens, learned from the first overflow error (text-based path only).
    /// </summary>
    private int? _knownContextLimit;

    /// <summary>
    /// Runs the LLM tool-calling loop.
    /// For native models (UseTextBasedToolCalling = false), delegates to
    /// <see cref="RockBotFunctionInvokingChatClient"/> which handles the full tool loop.
    /// For text-based models (UseTextBasedToolCalling = true), uses the manual loop
    /// that parses tool calls from free text.
    /// </summary>
    public async Task<string> RunAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        string? sessionId,
        ChatResponse? firstResponse = null,
        Func<string, CancellationToken, Task>? onPreToolCall = null,
        Func<string, CancellationToken, Task>? onProgress = null,
        Func<string, CancellationToken, Task>? onToolTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (modelBehavior.UseTextBasedToolCalling)
        {
            return await RunTextBasedLoopAsync(
                chatMessages, chatOptions, sessionId, firstResponse,
                onPreToolCall, onProgress, onToolTimeout, cancellationToken);
        }

        // Native path: FunctionInvokingChatClient handles the tool loop.
        // A single GetResponseAsync call executes all tool roundtrips via the middleware.
        logger.LogInformation(
            "Running native tool-calling path ({MessageCount} messages in context)",
            chatMessages.Count);

        // If there's a pre-fetched first response with tool calls, add it to history
        // and let the middleware continue from there.
        if (firstResponse is not null)
        {
            chatMessages.AddRange(firstResponse.Messages);
            logger.LogInformation("Added pre-fetched first response to context for native path");
        }

        var response = await llmClient.GetResponseAsync(chatMessages, chatOptions, cancellationToken);
        return ExtractAssistantText(response);
    }

    /// <summary>
    /// Text-based tool-calling loop for models that do not support native structured
    /// tool calling (e.g. DeepSeek). Parses tool calls from free text and manually
    /// invokes tools.
    /// </summary>
    private async Task<string> RunTextBasedLoopAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        string? sessionId,
        ChatResponse? firstResponse,
        Func<string, CancellationToken, Task>? onPreToolCall,
        Func<string, CancellationToken, Task>? onProgress,
        Func<string, CancellationToken, Task>? onToolTimeout,
        CancellationToken cancellationToken)
    {
        ChatResponse? pendingResponse = firstResponse;
        var anyToolCalled = false;
        var maxIterations = modelBehavior.MaxToolIterationsOverride ?? MaxToolIterations;
        var consecutiveTimeoutIterations = 0;

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
                    response = await llmClient.GetResponseAsync(chatMessages, chatOptions, cancellationToken);
                }
                catch (ClientResultException ex)
                    when (ex.Status == 400 && TryParseContextOverflow(ex.Message, out var max, out var used))
                {
                    _knownContextLimit = max;
                    logger.LogWarning(
                        "Context overflow ({Used:N0}/{Max:N0} tokens); trimming tool results and retrying once",
                        used, max);
                    TrimLargeToolResults(chatMessages, max);
                    response = await llmClient.GetResponseAsync(chatMessages, chatOptions, cancellationToken);
                }

                sw.Stop();
                logger.LogInformation(
                    "LLM responded in {ElapsedMs}ms — {MsgCount} message(s), iteration {Iteration}",
                    sw.ElapsedMilliseconds, response.Messages.Count, iteration + 2);
            }

            LogResponseMessages(response, iterationLabel: (iteration + 2).ToString());

            var functionCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

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

                    logger.LogInformation("Final response text ({Length} chars): {Preview}",
                        text.Length, text.Length > 200 ? text[..200] + "..." : text);
                    return text;
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
                        logger.LogWarning("Text tool call references unknown tool: {Name}", toolName);
                        chatMessages.Add(new ChatMessage(ChatRole.User,
                            $"[Tool result for {toolName}]: Error: unknown tool '{toolName}'"));
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

                    var toolSw = Stopwatch.StartNew();
                    object? result;
                    try
                    {
                        result = await tool.InvokeAsync(args, cancellationToken);
                        toolSw.Stop();
                        logger.LogInformation("Text-based tool {Name} returned in {ElapsedMs}ms: {Result}",
                            toolName, toolSw.ElapsedMilliseconds, result);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        toolSw.Stop();
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

                    // Chunking is handled by ChunkingAIFunction wrapper on the tool itself.
                    chatMessages.Add(new ChatMessage(ChatRole.User,
                        $"[Tool result for {toolName}]: {result?.ToString() ?? string.Empty}"));
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
                    logger.LogWarning("LLM requested unknown tool: {Name}", fc.Name);
                    chatMessages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(fc.CallId, $"Error: unknown tool '{fc.Name}'")]));
                    continue;
                }

                var args = fc.Arguments is not null
                    ? new AIFunctionArguments(fc.Arguments!)
                    : new AIFunctionArguments();
                var toolSw = Stopwatch.StartNew();
                object? result;
                try
                {
                    result = await tool.InvokeAsync(args, cancellationToken);
                    toolSw.Stop();
                    logger.LogInformation("Tool {Name} returned in {ElapsedMs}ms: {Result}",
                        fc.Name, toolSw.ElapsedMilliseconds, result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    toolSw.Stop();
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
                chatMessages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(fc.CallId, nativeResultStr)]));

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
                    return "I wasn't able to complete this task — the services I need aren't responding right now. " +
                           "Please try again in a few minutes.";
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
            summaryMessages, new ChatOptions(), cancellationToken);
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
                summaryMessages, new ChatOptions(), cancellationToken);
            forcedText = ExtractAssistantText(nudgedResponse);
        }

        if (!string.IsNullOrWhiteSpace(forcedText))
            return forcedText;

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
                    return fallback;
                }
            }
        }

        logger.LogWarning("Forced final response empty and no usable assistant history found; returning empty string");
        return string.Empty;
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

    // ── Logging ──────────────────────────────────────────────────────────────

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
        const string begin = "<｜tool▁calls▁begin｜>";
        var idx = text.IndexOf(begin, StringComparison.Ordinal);
        return idx >= 0 ? text[..idx] : text;
    }

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
}
