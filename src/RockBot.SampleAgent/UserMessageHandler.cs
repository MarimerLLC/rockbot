using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.SampleAgent;

/// <summary>
/// Handles incoming <see cref="UserMessage"/> by calling the LLM and publishing
/// an <see cref="AgentReply"/> back to the user. Maintains conversation history
/// and executes tool calls in an explicit loop for full control over the
/// call → tool → result → response cycle.
/// </summary>
internal sealed class UserMessageHandler(
    IChatClient chatClient,
    IMessagePublisher publisher,
    AgentIdentity agent,
    AgentProfile profile,
    ISystemPromptBuilder promptBuilder,
    IConversationMemory conversationMemory,
    IWorkingMemory workingMemory,
    ILongTermMemory longTermMemory,
    InjectedMemoryTracker injectedMemoryTracker,
    MemoryTools memoryTools,
    ILogger<UserMessageHandler> logger) : IMessageHandler<UserMessage>
{
    /// <summary>
    /// Maximum conversation turns to include in the LLM prompt.
    /// Keeps context size bounded regardless of how many turns are stored.
    /// </summary>
    private const int MaxLlmContextTurns = 20;

    /// <summary>
    /// Maximum number of tool-calling round-trips before forcing a final text response.
    /// </summary>
    private const int MaxToolIterations = 5;

    public async Task HandleAsync(UserMessage message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo ?? UserProxyTopics.UserResponse;
        var correlationId = context.Envelope.CorrelationId;

        logger.LogInformation("Received message from {UserId} in session {SessionId}: {Content}",
            message.UserId, message.SessionId, message.Content);

        try
        {
            // Record the incoming user turn
            await conversationMemory.AddTurnAsync(
                message.SessionId,
                new ConversationTurn("user", message.Content, DateTimeOffset.UtcNow),
                context.CancellationToken);

            // Build chat messages: system prompt + recent conversation history
            var systemPrompt = promptBuilder.Build(profile, agent);
            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt)
            };

            // Replay only recent history to keep LLM context bounded
            var history = await conversationMemory.GetTurnsAsync(
                message.SessionId, context.CancellationToken);

            var startIndex = Math.Max(0, history.Count - MaxLlmContextTurns);
            for (var i = startIndex; i < history.Count; i++)
            {
                var turn = history[i];
                var role = turn.Role == "user" ? ChatRole.User : ChatRole.Assistant;
                chatMessages.Add(new ChatMessage(role, turn.Content));
            }

            // BM25-score memories against the current message on every turn.
            // Filter to entries not yet injected this session (delta injection) — this naturally
            // handles topic drift: when the conversation shifts, newly relevant entries surface
            // without re-injecting context the LLM has already seen.
            // First turn only: fall back to most-recent entries when the message is too short to score.
            {
                var recalled = await longTermMemory.SearchAsync(
                    new MemorySearchCriteria(Query: message.Content, MaxResults: 8));

                if (recalled.Count == 0 && history.Count == 1)
                    recalled = await longTermMemory.SearchAsync(new MemorySearchCriteria(MaxResults: 5));

                var newEntries = recalled
                    .Where(e => injectedMemoryTracker.TryMarkAsInjected(message.SessionId, e.Id))
                    .ToList();

                if (newEntries.Count > 0)
                {
                    var lines = newEntries.Select(e =>
                        $"- [{e.Id}] ({e.Category ?? "general"}): {e.Content}");
                    var recallContext =
                        "Recalled from long-term memory (relevant to this message):\n" +
                        string.Join("\n", lines);
                    chatMessages.Add(new ChatMessage(ChatRole.System, recallContext));
                    logger.LogInformation(
                        "Injected {Count} new long-term memory entries (BM25 delta) for session {SessionId}",
                        newEntries.Count, message.SessionId);
                }
            }

            // Inject working memory inventory as a system message so the LLM knows what's cached
            var workingEntries = await workingMemory.ListAsync(message.SessionId);
            if (workingEntries.Count > 0)
            {
                var now = DateTimeOffset.UtcNow;
                var lines = workingEntries.Select(e =>
                {
                    var remaining = e.ExpiresAt - now;
                    var remainingStr = remaining.TotalMinutes >= 1
                        ? $"{(int)remaining.TotalMinutes}m{remaining.Seconds:D2}s"
                        : $"{Math.Max(0, remaining.Seconds)}s";
                    return $"- {e.Key}: expires in {remainingStr}";
                });
                var workingMemoryContext =
                    "Working memory (scratch space — use get_from_working_memory to retrieve):\n" +
                    string.Join("\n", lines);
                chatMessages.Add(new ChatMessage(ChatRole.System, workingMemoryContext));
                logger.LogInformation("Injected {Count} working memory entries into context", workingEntries.Count);
            }

            // Per-message working memory tools (session ID is baked in at construction)
            var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, message.SessionId, logger);

            var chatOptions = new ChatOptions
            {
                Tools = [..memoryTools.Tools, ..sessionWorkingMemoryTools.Tools]
            };

            // Tool-calling loop: call LLM, execute any tool calls, feed results back, repeat
            var content = await CallWithToolLoopAsync(chatMessages, chatOptions, context.CancellationToken);

            // Record the assistant turn (includes tool results incorporated by the LLM)
            await conversationMemory.AddTurnAsync(
                message.SessionId,
                new ConversationTurn("assistant", content, DateTimeOffset.UtcNow),
                context.CancellationToken);

            var reply = new AgentReply
            {
                Content = content,
                SessionId = message.SessionId,
                AgentName = agent.Name,
                IsFinal = true
            };

            var envelope = reply.ToEnvelope<AgentReply>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);

            logger.LogInformation("Published reply to {ReplyTo} for correlation {CorrelationId}",
                replyTo, correlationId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process user message {CorrelationId}", correlationId);

            var errorReply = new AgentReply
            {
                Content = $"Sorry, I encountered an error: {ex.Message}",
                SessionId = message.SessionId,
                AgentName = agent.Name,
                IsFinal = true
            };

            var envelope = errorReply.ToEnvelope<AgentReply>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);
        }
    }

    /// <summary>
    /// Calls the LLM and handles any tool calls in an explicit loop.
    /// After each LLM response, checks for <see cref="FunctionCallContent"/>; if present,
    /// executes the tools, appends results, and calls the LLM again. Repeats until the
    /// LLM returns a plain text response (no tool calls) or the iteration limit is reached.
    /// </summary>
    private async Task<string> CallWithToolLoopAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        // Log the tools being offered to the LLM
        var toolNames = chatOptions.Tools?
            .OfType<AIFunction>()
            .Select(t => t.Name)
            .ToList() ?? [];
        logger.LogInformation("Calling LLM with {ToolCount} tools: [{Tools}]",
            toolNames.Count, string.Join(", ", toolNames));

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var sw = Stopwatch.StartNew();
            var response = await chatClient.GetResponseAsync(
                chatMessages, chatOptions, cancellationToken);
            sw.Stop();

            // Log response structure for diagnostics
            logger.LogInformation(
                "LLM responded in {ElapsedMs}ms — {MsgCount} message(s), iteration {Iteration}",
                sw.ElapsedMilliseconds, response.Messages.Count, iteration + 1);

            for (var i = 0; i < response.Messages.Count; i++)
            {
                var msg = response.Messages[i];
                var contentParts = string.Join(", ", msg.Contents.Select(c => c.GetType().Name));
                logger.LogInformation(
                    "  Message[{Index}] role={Role} text={TextLen} chars, contents=[{ContentParts}]",
                    i, msg.Role, msg.Text?.Length ?? 0, contentParts);
            }

            // Collect any tool calls from the response
            var functionCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            logger.LogInformation("  FunctionCallContent count: {Count}", functionCalls.Count);

            if (functionCalls.Count == 0)
            {
                // No structured tool calls — check for text-based tool invocations
                // (some models write tool calls as plain text instead of using the API)
                var text = ExtractAssistantText(response);
                var knownTools = (chatOptions.Tools?
                    .OfType<AIFunction>()
                    .Select(t => t.Name)
                    ?? [])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var textCalls = ParseTextToolCalls(text, knownTools);

                if (textCalls.Count == 0)
                {
                    // True final response — no tool calls of any kind
                    logger.LogInformation("Final response text ({Length} chars): {Preview}",
                        text.Length, text.Length > 200 ? text[..200] + "..." : text);
                    return text;
                }

                logger.LogInformation(
                    "Detected {Count} text-based tool call(s) on iteration {Iteration}",
                    textCalls.Count, iteration + 1);

                // Add only the pre-tool portion as the assistant turn
                var preToolText = GetPreToolText(text);
                if (!string.IsNullOrWhiteSpace(preToolText))
                    chatMessages.Add(new ChatMessage(ChatRole.Assistant, preToolText));

                // Execute each tool and inject the real result
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
                    var result = await tool.InvokeAsync(args, cancellationToken);
                    toolSw.Stop();

                    logger.LogInformation("Text-based tool {Name} returned in {ElapsedMs}ms: {Result}",
                        toolName, toolSw.ElapsedMilliseconds, result);

                    chatMessages.Add(new ChatMessage(ChatRole.User,
                        $"[Tool result for {toolName}]: {result}"));
                }

                // Loop continues — call LLM again with actual tool results
                continue;
            }

            logger.LogInformation(
                "LLM requested {Count} tool call(s) on iteration {Iteration}",
                functionCalls.Count, iteration + 1);

            // Add the assistant message(s) containing the tool calls to the conversation
            chatMessages.AddRange(response.Messages);

            // Execute each tool call and feed results back
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
                var result = await tool.InvokeAsync(args, cancellationToken);
                toolSw.Stop();

                logger.LogInformation("Tool {Name} returned in {ElapsedMs}ms: {Result}",
                    fc.Name, toolSw.ElapsedMilliseconds, result);

                chatMessages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(fc.CallId, result)]));
            }

            // On the last iteration, remove tools so the LLM must produce a text response
            if (iteration == MaxToolIterations - 2)
            {
                chatOptions = new ChatOptions();
            }
        }

        // Exhausted iterations — one last call without tools to force a text response
        logger.LogWarning("Tool loop reached {Max} iterations; forcing final response", MaxToolIterations);
        var finalResponse = await chatClient.GetResponseAsync(
            chatMessages, new ChatOptions(), cancellationToken);
        return ExtractAssistantText(finalResponse);
    }

    /// <summary>
    /// Parses text-based tool invocations from a model response.
    /// Handles two formats that models may use instead of the API's structured function-call mechanism:
    /// <list type="bullet">
    ///   <item><c>tool_call_name: X</c> followed by <c>tool_call_arguments: {...}</c></item>
    ///   <item>A bare known tool name on its own line, optionally followed by a JSON args block</item>
    /// </list>
    /// </summary>
    private List<(string Name, string ArgsJson)> ParseTextToolCalls(string text, IReadOnlySet<string> knownTools)
    {
        var results = new List<(string, string)>();
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            // Strip leading markdown code-fence characters (``` or `) that some models
            // add when they mistakenly wrap the tool call in a fenced block.
            var cleanLine = line.TrimStart('`').Trim();

            // Format 1: tool_call_name: X followed by tool_call_arguments: {...}
            if (cleanLine.StartsWith("tool_call_name:", StringComparison.OrdinalIgnoreCase))
            {
                var toolName = cleanLine["tool_call_name:".Length..].Trim();
                if (string.IsNullOrEmpty(toolName))
                    continue;

                var argsJson = "{}";

                for (var j = i + 1; j < Math.Min(i + 4, lines.Length); j++)
                {
                    var argsLine = lines[j].Trim();
                    if (!argsLine.StartsWith("tool_call_arguments:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Strip any trailing code-fence chars the model may have appended
                    argsJson = argsLine["tool_call_arguments:".Length..].Trim().TrimEnd('`').Trim();

                    if (argsJson.StartsWith("{") && !IsBalancedJson(argsJson))
                    {
                        var sb = new System.Text.StringBuilder(argsJson);
                        for (var k = j + 1; k < lines.Length; k++)
                        {
                            sb.Append('\n').Append(lines[k]);
                            if (IsBalancedJson(sb.ToString()))
                                break;
                        }
                        argsJson = sb.ToString();
                    }

                    i = j; // skip past the consumed args line
                    break;
                }

                logger.LogDebug("Parsed tool_call_name format: {Name}({Args})", toolName, argsJson);
                results.Add((toolName, argsJson));
            }
            // Format 2: bare known tool name on its own line
            else if (knownTools.Contains(cleanLine))
            {
                var argsJson = "{}";

                // Check if the next non-empty line is a JSON args block
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

    /// <summary>Returns true when the string contains at least one '{' and all braces are balanced.</summary>
    private static bool IsBalancedJson(string s)
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

    /// <summary>Returns the portion of <paramref name="text"/> before the first tool invocation block.</summary>
    private static string GetPreToolText(string text)
    {
        var idx = text.IndexOf("tool_call_name:", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return text;

        // Walk back past any backtick code-fence characters on the same line as tool_call_name:
        while (idx > 0 && (text[idx - 1] == '`' || text[idx - 1] == ' '))
            idx--;

        return idx <= 0 ? text : text[..idx].TrimEnd();
    }

    /// <summary>Converts a <see cref="JsonElement"/> to its native .NET equivalent for AIFunctionArguments.</summary>
    private static object? ToNativeValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        _ => element.ToString()
    };

    /// <summary>
    /// Extracts text from the LLM response, walking backwards to find
    /// the last assistant message with non-empty text content.
    /// </summary>
    private string ExtractAssistantText(ChatResponse response)
    {
        for (var i = response.Messages.Count - 1; i >= 0; i--)
        {
            var msg = response.Messages[i];
            if (msg.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(msg.Text))
                return msg.Text.Trim();
        }

        // Fallback: concatenated text from all messages
        if (!string.IsNullOrWhiteSpace(response.Text))
            return response.Text.Trim();

        logger.LogWarning("LLM response contained no usable text across {Count} messages",
            response.Messages.Count);
        return string.Empty;
    }
}
