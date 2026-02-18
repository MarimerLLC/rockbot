using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.Cli;

/// <summary>
/// Handles incoming <see cref="UserMessage"/> by calling the LLM and publishing
/// an <see cref="AgentReply"/> back to the user. Maintains conversation history
/// and executes tool calls in an explicit loop for full control over the
/// call → tool → result → response cycle.
///
/// When the first LLM response contains tool calls, an immediate acknowledgment
/// (<see cref="AgentReply.IsFinal"/> = false) is published so the CLI can update
/// its spinner with the agent's words. The tool loop then continues in the
/// background, publishing the final reply (<see cref="AgentReply.IsFinal"/> = true)
/// with the same correlation ID when done.
/// </summary>
internal sealed class UserMessageHandler(
    ILlmClient llmClient,
    IMessagePublisher publisher,
    AgentIdentity agent,
    AgentProfile profile,
    ISystemPromptBuilder promptBuilder,
    IConversationMemory conversationMemory,
    IWorkingMemory workingMemory,
    ILongTermMemory longTermMemory,
    InjectedMemoryTracker injectedMemoryTracker,
    ISkillStore skillStore,
    SkillIndexTracker skillIndexTracker,
    MemoryTools memoryTools,
    IRulesStore rulesStore,
    RulesTools rulesTools,
    IToolRegistry toolRegistry,
    AgentClock clock,
    SkillTools skillTools,
    ILogger<UserMessageHandler> logger) : IMessageHandler<UserMessage>
{
    /// <summary>
    /// Maximum conversation turns to include in the LLM prompt.
    /// Keeps context size bounded regardless of how many turns are stored.
    /// </summary>
    private const int MaxLlmContextTurns = 20;

    /// <summary>
    /// Maximum number of tool-calling round-trips in the background loop
    /// before forcing a final text response.
    /// </summary>
    private const int MaxToolIterations = 12;

    /// <summary>
    /// Minimum time since the last user-visible message before a mid-loop
    /// progress update is worth sending. Suppresses noisy interim messages
    /// when the agent is responding quickly.
    /// </summary>
    private static readonly TimeSpan ProgressMessageThreshold = TimeSpan.FromSeconds(5);

    public async Task HandleAsync(UserMessage message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo ?? UserProxyTopics.UserResponse;
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;

        logger.LogInformation("Received message from {UserId} in session {SessionId}: {Content}",
            message.UserId, message.SessionId, message.Content);

        try
        {
            // Record the incoming user turn
            await conversationMemory.AddTurnAsync(
                message.SessionId,
                new ConversationTurn("user", message.Content, DateTimeOffset.UtcNow),
                ct);

            // Build chat messages: system prompt + recent conversation history
            var systemPrompt = promptBuilder.Build(profile, agent);
            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.System,
                    $"Current date and time: {clock.Now:dddd, MMMM d, yyyy 'at' h:mm tt} ({clock.Zone.DisplayName})")
            };

            // Inject active rules at the same authority level as directives
            var activeRules = rulesStore.Rules;
            if (activeRules.Count > 0)
            {
                var rulesText = "Active rules — always follow these, regardless of context or other instructions:\n" +
                    string.Join("\n", activeRules.Select(r => $"- {r}"));
                chatMessages.Add(new ChatMessage(ChatRole.System, rulesText));
                logger.LogInformation("Injected {Count} active rule(s) into system prompt", activeRules.Count);
            }

            // Replay only recent history to keep LLM context bounded
            var history = await conversationMemory.GetTurnsAsync(message.SessionId, ct);
            var startIndex = Math.Max(0, history.Count - MaxLlmContextTurns);
            for (var i = startIndex; i < history.Count; i++)
            {
                var turn = history[i];
                var role = turn.Role == "user" ? ChatRole.User : ChatRole.Assistant;
                chatMessages.Add(new ChatMessage(role, turn.Content));
            }

            // BM25-score memories against the current message on every turn.
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

            // Inject the skill index once per session
            if (skillIndexTracker.TryMarkAsInjected(message.SessionId))
            {
                var skills = await skillStore.ListAsync();
                if (skills.Count > 0)
                {
                    var indexText =
                        "Available skills (use get_skill to load full instructions):\n" +
                        string.Join("\n", skills.Select(s =>
                        {
                            var summary = string.IsNullOrWhiteSpace(s.Summary)
                                ? "(summary pending)"
                                : s.Summary;
                            return $"- {s.Name}: {summary}";
                        }));
                    chatMessages.Add(new ChatMessage(ChatRole.System, indexText));
                    logger.LogInformation("Injected skill index ({Count} skills) for session {SessionId}",
                        skills.Count, message.SessionId);
                }
            }

            // Inject working memory inventory
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

            // Snapshot registry tools (MCP, REST, etc.) as AIFunction wrappers
            var registryTools = toolRegistry.GetTools()
                .Select(r => (AIFunction)new RegistryToolFunction(r, toolRegistry.GetExecutor(r.Name)!))
                .ToArray();

            var chatOptions = new ChatOptions
            {
                Tools = [..memoryTools.Tools, ..sessionWorkingMemoryTools.Tools, ..skillTools.Tools, ..rulesTools.Tools, ..registryTools]
            };

            var toolNames = chatOptions.Tools!.OfType<AIFunction>().Select(t => t.Name).ToList();
            logger.LogInformation("Calling LLM with {ToolCount} tools: [{Tools}]",
                toolNames.Count, string.Join(", ", toolNames));

            // Log registry tool schemas at debug level so we can diagnose grammar-compiler
            // rejections (e.g. LM Studio "Channel Error" from unsupported schema features).
            if (logger.IsEnabled(LogLevel.Debug))
            {
                foreach (var rt in registryTools.OfType<RegistryToolFunction>())
                {
                    var schema = rt.JsonSchema;
                    logger.LogDebug("Registry tool schema [{Name}]: {Schema}",
                        rt.Name,
                        schema.ValueKind == JsonValueKind.Undefined ? "(undefined)" : schema.GetRawText());
                }
            }

            // First LLM call
            logger.LogInformation("Calling LLM — iteration 1 ({MessageCount} messages in context)",
                chatMessages.Count);
            var sw = Stopwatch.StartNew();
            var firstResponse = await llmClient.GetResponseAsync(chatMessages, chatOptions, ct);
            sw.Stop();

            logger.LogInformation(
                "LLM responded in {ElapsedMs}ms — {MsgCount} message(s), iteration 1",
                sw.ElapsedMilliseconds, firstResponse.Messages.Count);

            LogResponseMessages(firstResponse, iterationLabel: "1");

            // Detect whether the first response contains tool calls and extract ack text
            var (hasToolCalls, ackText) = GetFirstIterationAck(firstResponse, chatOptions);

            if (hasToolCalls)
            {
                // Publish an immediate acknowledgment (IsFinal=false) so the CLI spinner
                // can update from elapsed-time text to the agent's actual words.
                var effectiveAck = string.IsNullOrWhiteSpace(ackText)
                    ? "I'm working on that — I'll follow up shortly."
                    : ackText;

                logger.LogInformation(
                    "Tool calls detected on iteration 1; sending ack ({AckLen} chars) and continuing in background",
                    effectiveAck.Length);

                await PublishReplyAsync(effectiveAck, replyTo, correlationId, message.SessionId, isFinal: false, ct);

                // Continue the tool loop in the background.
                // BackgroundToolLoopAsync publishes IsFinal=true with the same correlationId when done.
                _ = BackgroundToolLoopAsync(
                    chatMessages, chatOptions, firstResponse,
                    message.SessionId, replyTo, correlationId, ct);
            }
            else
            {
                var text = ExtractAssistantText(firstResponse);

                // If the first response is an incomplete setup phrase (e.g. "Let me search:"),
                // the model intended to make a tool call but didn't. Treat it like a tool-call
                // response: publish an ACK and push into the background loop so it can be nudged.
                if (IsIncompleteSetupPhrase(text))
                {
                    logger.LogInformation(
                        "First response is an incomplete setup phrase ({Length} chars); routing to background loop",
                        text.Length);

                    await PublishReplyAsync(
                        "I'm working on that — I'll follow up shortly.",
                        replyTo, correlationId, message.SessionId, isFinal: false, ct);

                    _ = BackgroundToolLoopAsync(
                        chatMessages, chatOptions, firstResponse,
                        message.SessionId, replyTo, correlationId, ct);
                }
                else
                {
                    // No tool calls and a genuine final answer
                    await conversationMemory.AddTurnAsync(
                        message.SessionId,
                        new ConversationTurn("assistant", text, DateTimeOffset.UtcNow),
                        ct);

                    await PublishReplyAsync(text, replyTo, correlationId, message.SessionId, isFinal: true, ct);

                    logger.LogInformation("Published reply to {ReplyTo} for correlation {CorrelationId}",
                        replyTo, correlationId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // For API-level errors (e.g. 400 Bad Request), log the raw response body so we
            // can see the actual reason (invalid tool schema, context too long, etc.).
            if (ex is ClientResultException cre)
            {
                var body = cre.GetRawResponse()?.Content?.ToString();
                logger.LogWarning("LLM API error {Status}: {Body}", cre.Status, body);
            }

            logger.LogWarning(ex, "Failed to process user message {CorrelationId}", correlationId);

            var errorText = $"Sorry, I encountered an error: {ex.Message}";

            // Record the error reply as an assistant turn so conversation history stays in a
            // valid alternating user/assistant state. Without this, the next message would
            // send two consecutive user turns, which can itself cause a 400 and make the
            // failure sticky across all subsequent messages.
            try
            {
                await conversationMemory.AddTurnAsync(
                    message.SessionId,
                    new ConversationTurn("assistant", errorText, DateTimeOffset.UtcNow),
                    CancellationToken.None);
            }
            catch (Exception memEx)
            {
                logger.LogWarning(memEx, "Failed to record error assistant turn for session {SessionId}",
                    message.SessionId);
            }

            await PublishReplyAsync(errorText, replyTo, correlationId, message.SessionId, isFinal: true, ct);
        }
    }

    /// <summary>
    /// Continues the tool loop started in <see cref="HandleAsync"/> as a background task.
    /// Processes the tool calls from <paramref name="firstResponse"/>, executes further
    /// LLM iterations as needed, then publishes the final reply with <c>IsFinal=true</c>.
    /// </summary>
    private async Task BackgroundToolLoopAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        ChatResponse firstResponse,
        string sessionId,
        string replyTo,
        string? correlationId,
        CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Background tool loop started for session {SessionId}", sessionId);

            // Track when the last user-visible message was sent (the ACK went out just before this)
            // so we can suppress progress updates when the agent is responding quickly.
            var lastProgressAt = DateTimeOffset.UtcNow;

            var finalContent = await CallWithToolLoopAsync(chatMessages, chatOptions, firstResponse, ct,
                onProgress: async (msg, ct2) =>
                {
                    if (DateTimeOffset.UtcNow - lastProgressAt < ProgressMessageThreshold)
                        return;
                    await PublishReplyAsync(msg, replyTo, correlationId, sessionId, isFinal: false, ct2);
                    lastProgressAt = DateTimeOffset.UtcNow;
                });

            await conversationMemory.AddTurnAsync(
                sessionId,
                new ConversationTurn("assistant", finalContent, DateTimeOffset.UtcNow),
                ct);

            await PublishReplyAsync(finalContent, replyTo, correlationId, sessionId, isFinal: true, ct);

            logger.LogInformation(
                "Background tool loop published final reply for session {SessionId}", sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Background tool loop failed for session {SessionId}", sessionId);

            await PublishReplyAsync(
                $"Sorry, I ran into an error while working on your request: {ex.Message}",
                replyTo, correlationId, sessionId, isFinal: true, ct);
        }
    }

    /// <summary>
    /// Publishes an <see cref="AgentReply"/> to the given topic.
    /// </summary>
    private async Task PublishReplyAsync(
        string content, string replyTo, string? correlationId,
        string sessionId, bool isFinal, CancellationToken ct)
    {
        var reply = new AgentReply
        {
            Content = content,
            SessionId = sessionId,
            AgentName = agent.Name,
            IsFinal = isFinal
        };
        var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name, correlationId: correlationId);
        await publisher.PublishAsync(replyTo, envelope, ct);
    }

    /// <summary>
    /// Determines whether the first LLM response contains tool calls and extracts
    /// acknowledgment text to show the user while the background loop runs.
    /// For text-based tool calls, the ack is the text before the first tool invocation.
    /// For native tool calls, the ack is the full assistant text.
    /// </summary>
    private (bool hasToolCalls, string ackText) GetFirstIterationAck(
        ChatResponse response, ChatOptions chatOptions)
    {
        // Native function calls take priority
        var nativeCalls = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .ToList();

        if (nativeCalls.Count > 0)
            return (true, ExtractAssistantText(response));

        // Text-based tool calls
        var text = ExtractAssistantText(response);
        var knownTools = (chatOptions.Tools?.OfType<AIFunction>().Select(t => t.Name) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ParseTextToolCalls(text, knownTools).Count > 0)
            return (true, GetPreToolText(text));

        return (false, text);
    }

    /// <summary>
    /// Runs the tool-calling loop starting from a pre-fetched <paramref name="firstResponse"/>.
    /// Processes the tool calls contained in that response, then calls the LLM for subsequent
    /// iterations until no tool calls remain or <see cref="MaxToolIterations"/> is exhausted.
    /// </summary>
    private async Task<string> CallWithToolLoopAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        ChatResponse firstResponse,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? onProgress = null)
    {
        // pendingResponse holds the pre-fetched first response for iteration 0.
        // After that it's null, and the loop calls the LLM for each iteration.
        ChatResponse? pendingResponse = firstResponse;

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
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
                logger.LogInformation("Calling LLM — iteration {Iteration} ({MessageCount} messages in context)",
                    iteration + 2, chatMessages.Count);
                var sw = Stopwatch.StartNew();
                response = await llmClient.GetResponseAsync(chatMessages, chatOptions, cancellationToken);
                sw.Stop();

                logger.LogInformation(
                    "LLM responded in {ElapsedMs}ms — {MsgCount} message(s), iteration {Iteration}",
                    sw.ElapsedMilliseconds, response.Messages.Count, iteration + 2);
            }

            LogResponseMessages(response, iterationLabel: (iteration + 2).ToString());

            // Collect any native tool calls from the response
            var functionCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            logger.LogInformation("  FunctionCallContent count: {Count}", functionCalls.Count);

            if (functionCalls.Count == 0)
            {
                // No native tool calls — check for text-based tool invocations
                var text = ExtractAssistantText(response);
                var knownTools = (chatOptions.Tools?
                    .OfType<AIFunction>()
                    .Select(t => t.Name)
                    ?? [])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var textCalls = ParseTextToolCalls(text, knownTools);

                if (textCalls.Count == 0)
                {
                    // Check whether the response looks like an incomplete setup phrase
                    // (e.g. "Let me search for that:" or "Now I'll try:") rather than a
                    // genuine final answer. A trailing colon or ellipsis is a strong signal
                    // the LLM intended to follow up with a tool call but didn't produce one.
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

                    // True final response
                    logger.LogInformation("Final response text ({Length} chars): {Preview}",
                        text.Length, text.Length > 200 ? text[..200] + "..." : text);
                    return text;
                }

                logger.LogInformation(
                    "Detected {Count} text-based tool call(s) on iteration {Iteration}",
                    textCalls.Count, iteration + 2);

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
                    }

                    chatMessages.Add(new ChatMessage(ChatRole.User,
                        $"[Tool result for {toolName}]: {result}"));
                }

                if (onProgress is not null)
                {
                    var names = string.Join(", ", textCalls.Select(t => t.Name));
                    await onProgress($"Called {names}. Still working…", cancellationToken);
                }

                continue;
            }

            logger.LogInformation(
                "LLM requested {Count} tool call(s) on iteration {Iteration}",
                functionCalls.Count, iteration + 2);

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
                }

                chatMessages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(fc.CallId, result)]));
            }

            if (onProgress is not null)
            {
                var names = string.Join(", ", functionCalls.Select(f => f.Name));
                await onProgress($"Called {names}. Still working…", cancellationToken);
            }

            // On the last iteration, remove tools so the LLM must produce a text response
            if (iteration == MaxToolIterations - 2)
                chatOptions = new ChatOptions();
        }

        // Exhausted iterations — one last call without tools to force a text response
        logger.LogWarning("Tool loop reached {Max} iterations; forcing final response", MaxToolIterations);
        var finalResponse = await llmClient.GetResponseAsync(
            chatMessages, new ChatOptions(), cancellationToken);
        return ExtractAssistantText(finalResponse);
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

    /// <summary>
    /// Returns true when the response text looks like an incomplete agentic setup phrase
    /// rather than a genuine final answer. Detects cases where the LLM says what it's
    /// about to do (e.g. "Let me search for that:") without actually emitting a tool call.
    /// A trailing colon or ellipsis after trimming is the primary signal.
    /// </summary>
    private static bool IsIncompleteSetupPhrase(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.EndsWith(':') || trimmed.EndsWith("...");
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

    /// <summary>
    /// Wraps a <see cref="ToolRegistration"/> + <see cref="IToolExecutor"/> as an <see cref="AIFunction"/>
    /// so registry tools (e.g. MCP) can be passed directly to the LLM via <see cref="ChatOptions.Tools"/>.
    /// </summary>
    private sealed class RegistryToolFunction(ToolRegistration registration, IToolExecutor executor) : AIFunction
    {
        private static readonly JsonSerializerOptions SerializerOptions = new();

        /// <summary>
        /// Minimal valid OpenAI tool schema used as a fallback when a tool has no schema
        /// or an unparseable one. LM Studio's grammar compiler requires at minimum
        /// <c>{"type":"object","properties":{}}</c> — returning <c>default(JsonElement)</c>
        /// causes a Channel Error.
        /// </summary>
        private static readonly JsonElement FallbackSchema =
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;

        public override string Name => registration.Name;
        public override string Description => registration.Description;

        public override JsonElement JsonSchema
        {
            get
            {
                if (string.IsNullOrEmpty(registration.ParametersSchema)) return FallbackSchema;
                try { return JsonDocument.Parse(registration.ParametersSchema).RootElement; }
                catch { return FallbackSchema; }
            }
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            string? argsJson = null;
            if (arguments is { Count: > 0 })
            {
                argsJson = JsonSerializer.Serialize(
                    arguments.ToDictionary(k => k.Key, k => k.Value),
                    SerializerOptions);
            }

            var request = new ToolInvokeRequest
            {
                ToolCallId = Guid.NewGuid().ToString("N"),
                ToolName = registration.Name,
                Arguments = argsJson
            };

            var response = await executor.ExecuteAsync(request, cancellationToken);
            return response.IsError ? $"Error: {response.Content}" : response.Content;
        }
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
                return StripModelToolTokens(msg.Text).Trim();
        }

        // Fallback: concatenated text from all messages
        if (!string.IsNullOrWhiteSpace(response.Text))
            return StripModelToolTokens(response.Text).Trim();

        logger.LogWarning("LLM response contained no usable text across {Count} messages",
            response.Messages.Count);
        return string.Empty;
    }

    /// <summary>
    /// Strips model-internal tool-call token sequences from assistant text so they
    /// are never surfaced to the user. Handles Qwen-style delimiters
    /// (<c>&lt;｜tool▁calls▁begin｜&gt;…&lt;｜tool▁calls▁end｜&gt;</c>).
    /// </summary>
    private static string StripModelToolTokens(string text)
    {
        const string begin = "<｜tool▁calls▁begin｜>";
        var idx = text.IndexOf(begin, StringComparison.Ordinal);
        return idx >= 0 ? text[..idx] : text;
    }
}
