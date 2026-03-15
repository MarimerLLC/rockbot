using System.ClientModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles incoming <see cref="UserMessage"/> by calling the LLM and publishing
/// an <see cref="AgentReply"/> back to the user.
/// </summary>
internal sealed class UserMessageHandler(
    ILlmClient llmClient,
    ILlmTierSelector tierSelector,
    TieredChatClientRegistry registry,
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
    SkillRecallTracker skillRecallTracker,
    MemoryTools memoryTools,
    IRulesStore rulesStore,
    RulesTools rulesTools,
    IToolRegistry toolRegistry,
    AgentClock clock,
    ToolGuideTools toolGuideTools,
    ModelBehavior modelBehavior,
    IFeedbackStore feedbackStore,
    IUserActivityMonitor userActivityMonitor,
    IAgentWorkSerializer workSerializer,
    AgentLoopRunner agentLoopRunner,
    AgentContextBuilder agentContextBuilder,
    SessionBackgroundTaskTracker sessionTracker,
    SessionStartTracker sessionStartTracker,
    IOptions<AgentProfileOptions> profileOptions,
    ILogger<UserMessageHandler> logger,
    TierRoutingLogger tierRoutingLogger,
    ISkillUsageStore? skillUsageStore = null) : IMessageHandler<UserMessage>
{
    private static readonly TimeSpan ProgressMessageThreshold = TimeSpan.FromSeconds(5);

    // Shared with AgentLoopRunner — single source of truth for hallucinated-action detection.
    private static readonly Regex HallucinatedActionRegex = AgentLoopRunner.HallucinatedActionRegex;

    private static readonly Regex CorrectionRegex = new(
        @"\b(no[,\s]|that'?s?\s+(wrong|incorrect|not right)|you were wrong|actually[,\s]|that didn'?t work|try again)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task HandleAsync(UserMessage message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo ?? UserProxyTopics.UserResponse;
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;

        userActivityMonitor.RecordActivity();

        // Cancel any background loop still running for this session from a prior message.
        // This prevents stale tool calls (e.g. sending an email from a previous topic)
        // from executing after the user has already moved on.
        var sessionCt = sessionTracker.BeginSession(message.SessionId, ct);
        logger.LogInformation("Received message from {UserId} in session {SessionId}: {Content}",
            message.UserId, message.SessionId, message.Content);

        var classification = tierSelector.Classify(message.Content);
        var tier = classification.Tier;
        logger.LogInformation("Routing user message to tier={Tier} (score={Score:F3})", tier, classification.ComplexityScore);
        var turnSw = System.Diagnostics.Stopwatch.StartNew();
        var tierTag = new KeyValuePair<string, object?>("rockbot.llm.tier", tier.ToString());

        // Start the turn span. For background paths it outlives this method — we pass
        // it to the background task which disposes it when the final reply is published.
        var turnId = Guid.NewGuid().ToString("N")[..16];
        var modelId = registry.GetModelId(tier) ?? tier.ToString();
        var turnActivity = HostDiagnostics.Source.StartActivity("rockbot.turn");
        turnActivity?.SetTag("rockbot.llm.tier", tier.ToString());
        turnActivity?.SetTag("rockbot.llm.model", modelId);
        turnActivity?.SetTag("rockbot.turn.id", turnId);
        turnActivity?.SetTag("rockbot.user.id", message.UserId);
        turnActivity?.SetTag("rockbot.session.id", message.SessionId);
        turnActivity?.SetTag("rockbot.agent.name", agent.Name);
        turnActivity?.SetTag("rockbot.message.preview",
            message.Content.Length > 100 ? message.Content[..100] : message.Content);
        var turnActivityHandedOff = false;

        try
        {
            await conversationMemory.AddTurnAsync(
                message.SessionId,
                new ConversationTurn("user", message.Content, DateTimeOffset.UtcNow),
                ct);

            if (CorrectionRegex.IsMatch(message.Content))
            {
                _ = feedbackStore.AppendAsync(new FeedbackEntry(
                    Id: Guid.NewGuid().ToString("N")[..12],
                    SessionId: message.SessionId,
                    SignalType: FeedbackSignalType.Correction,
                    Summary: "User message detected as a correction",
                    Detail: message.Content.Length > 200 ? message.Content[..200] : message.Content,
                    Timestamp: DateTimeOffset.UtcNow));
            }

            // Build context using shared builder
            var chatMessages = await agentContextBuilder.BuildAsync(message.SessionId, message.Content, ct);
            var postInjectionTokenEstimate = EstimateContextTokens(chatMessages);
            HostDiagnostics.TurnContextTokens.Record(postInjectionTokenEstimate, tierTag);

            // Session-start briefing: on the first turn of a new session, inject the
            // session-start directive so the agent checks briefing queue, plans, etc.
            if (sessionStartTracker.TryMarkAsFirstTurn(message.SessionId))
            {
                var sessionStartPath = Path.Combine(profileOptions.Value.BasePath, "session-start.md");
                if (File.Exists(sessionStartPath))
                {
                    var sessionStartContent = await File.ReadAllTextAsync(sessionStartPath, ct);
                    chatMessages.Insert(1, new ChatMessage(ChatRole.System, sessionStartContent));
                    logger.LogInformation("Injected session-start directive for session {SessionId}", message.SessionId);
                }
            }

            // Per-message working memory tools — namespace scoped to this session
            var sessionNamespace = $"session/{message.SessionId}";
            var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, sessionNamespace, logger);

            // Per-session skill tools with usage tracking
            var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, message.SessionId, skillUsageStore);

            // Registry tools (MCP, REST, etc.)
            var registryTools = toolRegistry.GetTools()
                .Select(r => (AIFunction)new RegistryToolFunction(r, toolRegistry.GetExecutor(r.Name)!, sessionNamespace))
                .ToArray();

            var allTools = memoryTools.Tools
                .Concat(sessionWorkingMemoryTools.Tools)
                .Concat(sessionSkillTools.Tools)
                .Concat(rulesTools.Tools)
                .Concat(toolGuideTools.Tools)
                .Concat(registryTools)
                .OfType<AIFunction>()
                .WithChunking(workingMemory, sessionNamespace, modelBehavior, logger);

            var chatOptions = new ChatOptions
            {
                Tools = allTools
            };

            var toolNames = chatOptions.Tools!.OfType<AIFunction>().Select(t => t.Name).ToList();
            logger.LogInformation("Calling LLM with {ToolCount} tools: [{Tools}]",
                toolNames.Count, string.Join(", ", toolNames));

            if (logger.IsEnabled(LogLevel.Debug))
            {
                foreach (var rt in registryTools.OfType<RegistryToolFunction>())
                {
                    var schema = rt.JsonSchema;
                    logger.LogDebug("Registry tool schema [{Name}]: {Schema}",
                        rt.Name,
                        schema.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "(undefined)" : schema.GetRawText());
                }
            }

            if (!modelBehavior.UseTextBasedToolCalling)
            {
                // Native path: fire-and-forget the LLM call so HandleAsync returns promptly,
                // allowing RabbitMQ to ack the UserMessage before tool calls run.
                // This prevents subagent re-spawn on pod restart (issue #122).
                logger.LogInformation("Native path: launching background LLM loop for session {SessionId}", message.SessionId);
                await PublishReplyAsync("I'm working on that — I'll follow up shortly.",
                    replyTo, correlationId, message.SessionId, isFinal: false, ct);
                turnActivityHandedOff = true;
                _ = NativeLlmLoopAsync(chatMessages, chatOptions, classification, postInjectionTokenEstimate,
                    message.SessionId, replyTo, correlationId, turnActivity, sessionCt);
            }
            else
            {
                logger.LogInformation("Calling LLM — iteration 1 ({MessageCount} messages in context)",
                    chatMessages.Count);
                var routingSw = System.Diagnostics.Stopwatch.StartNew();
                var firstResponse = await llmClient.GetResponseAsync(chatMessages, tier, chatOptions, ct);
                routingSw.Stop();

                logger.LogInformation(
                    "LLM responded in {ElapsedMs}ms — {MsgCount} message(s), iteration 1",
                    routingSw.ElapsedMilliseconds, firstResponse.Messages.Count);

                // Log response messages
                for (var i = 0; i < firstResponse.Messages.Count; i++)
                {
                    var msg = firstResponse.Messages[i];
                    var contentParts = string.Join(", ", msg.Contents.Select(c => c.GetType().Name));
                    logger.LogInformation(
                        "  Message[{Index}] role={Role} text={TextLen} chars, contents=[{ContentParts}]",
                        i, msg.Role, msg.Text?.Length ?? 0, contentParts);
                }

                // Text-based path: check whether the first response contains tool
                // calls that still need to be executed by the manual loop.
                var (hasToolCalls, ackText) = GetFirstIterationAck(firstResponse, chatOptions);

                // Routing telemetry written here for the text-based path (first iteration complete)
                _ = tierRoutingLogger.AppendAsync(new TierRoutingEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    PromptPreview = message.Content.Length > 150 ? message.Content[..150] : message.Content,
                    Tier = tier,
                    Context = "user-message",
                    ComplexityScore = classification.ComplexityScore,
                    MatchedHighKeywords = classification.MatchedHighKeywords,
                    MatchedLowKeywords = classification.MatchedLowKeywords,
                    PostInjectionTokenEstimate = postInjectionTokenEstimate,
                    InputTokens = firstResponse.Usage?.InputTokenCount,
                    OutputTokens = firstResponse.Usage?.OutputTokenCount,
                    LatencyMs = routingSw.ElapsedMilliseconds,
                });

                if (hasToolCalls)
                {
                    var effectiveAck = string.IsNullOrWhiteSpace(ackText)
                        ? "I'm working on that — I'll follow up shortly."
                        : ackText;

                    logger.LogInformation(
                        "Tool calls detected on iteration 1; sending ack ({AckLen} chars) and continuing in background",
                        effectiveAck.Length);

                    await PublishReplyAsync(effectiveAck, replyTo, correlationId, message.SessionId, isFinal: false, ct);

                    turnActivityHandedOff = true;
                    _ = BackgroundToolLoopAsync(
                        chatMessages, chatOptions, firstResponse, tier,
                        message.SessionId, replyTo, correlationId, turnActivity, sessionCt);
                }
                else
                {
                    var text = agentLoopRunner.ExtractAssistantText(firstResponse);

                    if (AgentLoopRunner.IsIncompleteSetupPhrase(text))
                    {
                        logger.LogInformation(
                            "First response is an incomplete setup phrase ({Length} chars); routing to background loop",
                            text.Length);

                        await PublishReplyAsync(
                            "I'm working on that — I'll follow up shortly.",
                            replyTo, correlationId, message.SessionId, isFinal: false, ct);

                        turnActivityHandedOff = true;
                        _ = BackgroundToolLoopAsync(
                            chatMessages, chatOptions, firstResponse, tier,
                            message.SessionId, replyTo, correlationId, turnActivity, sessionCt);
                    }
                    else if (modelBehavior.NudgeOnHallucinatedToolCalls
                        && (HallucinatedActionRegex.IsMatch(text) || AgentLoopRunner.CapabilityDenialRegex.IsMatch(text)))
                    {
                        logger.LogWarning(
                            "Hallucinated action or capability denial on first response ({Length} chars); routing to background loop for nudge",
                            text.Length);

                        await PublishReplyAsync(
                            "I'm working on that — I'll follow up shortly.",
                            replyTo, correlationId, message.SessionId, isFinal: false, ct);

                        turnActivityHandedOff = true;
                        _ = BackgroundToolLoopAsync(
                            chatMessages, chatOptions, firstResponse, tier,
                            message.SessionId, replyTo, correlationId, turnActivity, sessionCt);
                    }
                    else
                    {
                        await conversationMemory.AddTurnAsync(
                            message.SessionId,
                            new ConversationTurn("assistant", text, DateTimeOffset.UtcNow),
                            ct);

                        await PublishReplyAsync(text, replyTo, correlationId, message.SessionId, isFinal: true, ct);
                        turnActivity?.SetTag("rockbot.turn.status", "ok");
                        turnActivity?.SetStatus(ActivityStatusCode.Ok);
                        turnSw.Stop();
                        HostDiagnostics.TurnDuration.Record(turnSw.Elapsed.TotalMilliseconds, tierTag,
                            new KeyValuePair<string, object?>("rockbot.turn.status", "ok"));
                        HostDiagnostics.Turns.Add(1, tierTag,
                            new KeyValuePair<string, object?>("rockbot.turn.status", "ok"));

                        logger.LogInformation("Published reply to {ReplyTo} for correlation {CorrelationId}",
                            replyTo, correlationId);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is ClientResultException cre)
            {
                var body = cre.GetRawResponse()?.Content?.ToString();
                logger.LogWarning("LLM API error {Status}: {Body}", cre.Status, body);
            }

            logger.LogWarning(ex, "Failed to process user message {CorrelationId}", correlationId);

            var errorText = $"Sorry, I encountered an error: {ex.Message}";

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
            turnActivity?.SetTag("rockbot.turn.status", "error");
            turnActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            turnSw.Stop();
            HostDiagnostics.TurnDuration.Record(turnSw.Elapsed.TotalMilliseconds, tierTag,
                new KeyValuePair<string, object?>("rockbot.turn.status", "error"));
            HostDiagnostics.Turns.Add(1, tierTag,
                new KeyValuePair<string, object?>("rockbot.turn.status", "error"));
        }
        finally
        {
            // Dispose the turn span only when this method owns it (sync paths).
            // Background paths pass it to the background task which disposes it.
            if (!turnActivityHandedOff)
                turnActivity?.Dispose();
        }
    }

    private async Task NativeLlmLoopAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        TierClassification classification,
        int? postInjectionTokenEstimate,
        string sessionId,
        string replyTo,
        string? correlationId,
        System.Diagnostics.Activity? turnActivity,
        CancellationToken ct)
    {
        var loopSw = System.Diagnostics.Stopwatch.StartNew();
        var nativeTierTag = new KeyValuePair<string, object?>("rockbot.llm.tier", classification.Tier.ToString());
        try
        {
            await using var slot = await workSerializer.AcquireForUserAsync(ct);

            logger.LogInformation("Native LLM loop started for session {SessionId}", sessionId);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await llmClient.GetResponseAsync(chatMessages, classification.Tier, chatOptions, ct);
            sw.Stop();

            logger.LogInformation(
                "LLM responded in {ElapsedMs}ms — {MsgCount} message(s)",
                sw.ElapsedMilliseconds, response.Messages.Count);

            var text = agentLoopRunner.ExtractAssistantText(response);

            var toolCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();
            var toolCallNames = toolCalls.Select(c => c.Name).Distinct().ToList();

            // If the model made no tool calls and claimed it lacks a service, nudge once.
            if (toolCalls.Count == 0
                && modelBehavior.NudgeOnHallucinatedToolCalls
                && AgentLoopRunner.CapabilityDenialRegex.IsMatch(text))
            {
                logger.LogWarning(
                    "Capability denial in native path ({Length} chars); nudging to check available services",
                    text.Length);
                chatMessages.AddRange(response.Messages);
                chatMessages.Add(new ChatMessage(ChatRole.User, AgentLoopRunner.CapabilityDenialNudge));
                var retryResponse = await llmClient.GetResponseAsync(chatMessages, classification.Tier, chatOptions, ct);
                text = agentLoopRunner.ExtractAssistantText(retryResponse);
                var retryToolCalls = retryResponse.Messages
                    .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                    .Count();
                logger.LogInformation(
                    "Capability denial nudge complete — {ToolCallCount} tool call(s) on retry, {TextLen} chars",
                    retryToolCalls, text.Length);
                toolCalls = retryResponse.Messages
                    .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                    .ToList();
                toolCallNames = toolCalls.Select(c => c.Name).Distinct().ToList();
            }

            logger.LogInformation(
                "Native path complete — {ToolCallCount} tool call(s) resolved, final text {TextLen} chars",
                toolCalls.Count, text.Length);

            _ = tierRoutingLogger.AppendAsync(new TierRoutingEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                PromptPreview = chatMessages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text is { } p
                    ? (p.Length > 150 ? p[..150] : p)
                    : "",
                Tier = classification.Tier,
                Context = "user-message",
                ComplexityScore = classification.ComplexityScore,
                MatchedHighKeywords = classification.MatchedHighKeywords,
                MatchedLowKeywords = classification.MatchedLowKeywords,
                PostInjectionTokenEstimate = postInjectionTokenEstimate,
                InputTokens = response.Usage?.InputTokenCount,
                OutputTokens = response.Usage?.OutputTokenCount,
                LatencyMs = sw.ElapsedMilliseconds,
                ToolCallCount = toolCalls.Count,
                ToolsUsed = toolCallNames.Count > 0 ? toolCallNames : null,
            });

            text = ResponseSanitizer.StripTrailingOffers(text);

            await conversationMemory.AddTurnAsync(
                sessionId,
                new ConversationTurn("assistant", text, DateTimeOffset.UtcNow),
                ct);

            await PublishReplyAsync(text, replyTo, correlationId, sessionId, isFinal: true, ct);
            loopSw.Stop();
            turnActivity?.SetTag("rockbot.turn.status", "ok");
            turnActivity?.SetStatus(ActivityStatusCode.Ok);
            HostDiagnostics.TurnDuration.Record(loopSw.Elapsed.TotalMilliseconds, nativeTierTag,
                new KeyValuePair<string, object?>("rockbot.turn.status", "ok"));
            HostDiagnostics.Turns.Add(1, nativeTierTag,
                new KeyValuePair<string, object?>("rockbot.turn.status", "ok"));

            logger.LogInformation("Published reply to {ReplyTo} for correlation {CorrelationId}",
                replyTo, correlationId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Native LLM loop failed for session {SessionId}", sessionId);

            await PublishReplyAsync(
                $"Sorry, I ran into an error while working on your request: {ex.Message}",
                replyTo, correlationId, sessionId, isFinal: true, ct);
            loopSw.Stop();
            turnActivity?.SetTag("rockbot.turn.status", "error");
            turnActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            HostDiagnostics.TurnDuration.Record(loopSw.Elapsed.TotalMilliseconds, nativeTierTag,
                new KeyValuePair<string, object?>("rockbot.turn.status", "error"));
            HostDiagnostics.Turns.Add(1, nativeTierTag,
                new KeyValuePair<string, object?>("rockbot.turn.status", "error"));
        }
        finally
        {
            turnActivity?.Dispose();
        }
    }

    private async Task BackgroundToolLoopAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        ChatResponse firstResponse,
        ModelTier tier,
        string sessionId,
        string replyTo,
        string? correlationId,
        System.Diagnostics.Activity? turnActivity,
        CancellationToken ct)
    {
        var loopSw = System.Diagnostics.Stopwatch.StartNew();
        var bgTierTag = new KeyValuePair<string, object?>("rockbot.llm.tier", tier.ToString());
        try
        {
            // Acquire the single execution slot, preempting any running scheduled
            // task. If this session itself is cancelled (new user message) while
            // waiting, the await throws OperationCanceledException and we exit.
            await using var slot = await workSerializer.AcquireForUserAsync(ct);

            logger.LogInformation("Background tool loop started for session {SessionId}", sessionId);

            var lastProgressAt = DateTimeOffset.UtcNow;

            var finalContent = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, sessionId, firstResponse: firstResponse, tier: tier,
                onPreToolCall: async (desc, ct2) =>
                {
                    await PublishReplyAsync($"Working on it — checking {desc}…", replyTo, correlationId, sessionId, isFinal: false, ct2);
                    lastProgressAt = DateTimeOffset.UtcNow;
                },
                onProgress: async (msg, ct2) =>
                {
                    if (DateTimeOffset.UtcNow - lastProgressAt < ProgressMessageThreshold)
                        return;
                    await PublishReplyAsync(msg, replyTo, correlationId, sessionId, isFinal: false, ct2);
                    lastProgressAt = DateTimeOffset.UtcNow;
                },
                onToolTimeout: async (desc, ct2) =>
                {
                    await PublishReplyAsync(
                        $"The {desc} service is taking too long to respond — trying a different approach…",
                        replyTo, correlationId, sessionId, isFinal: false, ct2);
                    lastProgressAt = DateTimeOffset.UtcNow;
                },
                cancellationToken: ct);

            await conversationMemory.AddTurnAsync(
                sessionId,
                new ConversationTurn("assistant", finalContent, DateTimeOffset.UtcNow),
                ct);

            await PublishReplyAsync(finalContent, replyTo, correlationId, sessionId, isFinal: true, ct);
            loopSw.Stop();
            turnActivity?.SetTag("rockbot.turn.status", "ok");
            turnActivity?.SetStatus(ActivityStatusCode.Ok);
            HostDiagnostics.TurnDuration.Record(loopSw.Elapsed.TotalMilliseconds, bgTierTag,
                new KeyValuePair<string, object?>("rockbot.turn.status", "ok"));
            HostDiagnostics.Turns.Add(1, bgTierTag,
                new KeyValuePair<string, object?>("rockbot.turn.status", "ok"));

            logger.LogInformation(
                "Background tool loop published final reply for session {SessionId}", sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Background tool loop failed for session {SessionId}", sessionId);

            await PublishReplyAsync(
                $"Sorry, I ran into an error while working on your request: {ex.Message}",
                replyTo, correlationId, sessionId, isFinal: true, ct);
            loopSw.Stop();
            turnActivity?.SetTag("rockbot.turn.status", "error");
            turnActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            HostDiagnostics.TurnDuration.Record(loopSw.Elapsed.TotalMilliseconds, bgTierTag,
                new KeyValuePair<string, object?>("rockbot.turn.status", "error"));
            HostDiagnostics.Turns.Add(1, bgTierTag,
                new KeyValuePair<string, object?>("rockbot.turn.status", "error"));
        }
        finally
        {
            turnActivity?.Dispose();
        }
    }

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
    /// Estimates the total token count of a built context by summing character lengths and
    /// dividing by 4 (the conventional rough approximation for English-language text).
    /// Used to detect "token surprise" misroutes in the dream feedback loop.
    /// </summary>
    private static int EstimateContextTokens(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m => (m.Text?.Length ?? 0) / 4 + 1);

    private (bool hasToolCalls, string ackText) GetFirstIterationAck(
        ChatResponse response, ChatOptions chatOptions)
    {
        var nativeCalls = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .ToList();

        if (nativeCalls.Count > 0)
            return (true, agentLoopRunner.ExtractAssistantText(response));

        var text = agentLoopRunner.ExtractAssistantText(response);
        var knownTools = (chatOptions.Tools?.OfType<AIFunction>().Select(t => t.Name) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (agentLoopRunner.ParseTextToolCalls(text, knownTools).Count > 0)
            return (true, AgentLoopRunner.GetPreToolText(text));

        return (false, text);
    }
}
