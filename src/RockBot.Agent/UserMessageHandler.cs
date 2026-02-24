using System.ClientModel;
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

        var tier = tierSelector.SelectTier(message.Content);
        logger.LogInformation("Routing user message to tier={Tier}", tier);

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

            logger.LogInformation("Calling LLM — iteration 1 ({MessageCount} messages in context)",
                chatMessages.Count);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var firstResponse = await llmClient.GetResponseAsync(chatMessages, tier, chatOptions, ct);
            sw.Stop();

            logger.LogInformation(
                "LLM responded in {ElapsedMs}ms — {MsgCount} message(s), iteration 1",
                sw.ElapsedMilliseconds, firstResponse.Messages.Count);

            // Log response messages
            for (var i = 0; i < firstResponse.Messages.Count; i++)
            {
                var msg = firstResponse.Messages[i];
                var contentParts = string.Join(", ", msg.Contents.Select(c => c.GetType().Name));
                logger.LogInformation(
                    "  Message[{Index}] role={Role} text={TextLen} chars, contents=[{ContentParts}]",
                    i, msg.Role, msg.Text?.Length ?? 0, contentParts);
            }

            if (!modelBehavior.UseTextBasedToolCalling)
            {
                // Native path: FunctionInvokingChatClient already executed all tool
                // calls during the GetResponseAsync above. The response is complete —
                // just extract the final assistant text and publish it.
                var text = agentLoopRunner.ExtractAssistantText(firstResponse);

                var toolCallCount = firstResponse.Messages
                    .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                    .Count();

                logger.LogInformation(
                    "Native path complete — {ToolCallCount} tool call(s) resolved, final text {TextLen} chars",
                    toolCallCount, text.Length);

                await conversationMemory.AddTurnAsync(
                    message.SessionId,
                    new ConversationTurn("assistant", text, DateTimeOffset.UtcNow),
                    ct);

                await PublishReplyAsync(text, replyTo, correlationId, message.SessionId, isFinal: true, ct);

                logger.LogInformation("Published reply to {ReplyTo} for correlation {CorrelationId}",
                    replyTo, correlationId);
            }
            else
            {
                // Text-based path: check whether the first response contains tool
                // calls that still need to be executed by the manual loop.
                var (hasToolCalls, ackText) = GetFirstIterationAck(firstResponse, chatOptions);

                if (hasToolCalls)
                {
                    var effectiveAck = string.IsNullOrWhiteSpace(ackText)
                        ? "I'm working on that — I'll follow up shortly."
                        : ackText;

                    logger.LogInformation(
                        "Tool calls detected on iteration 1; sending ack ({AckLen} chars) and continuing in background",
                        effectiveAck.Length);

                    await PublishReplyAsync(effectiveAck, replyTo, correlationId, message.SessionId, isFinal: false, ct);

                    _ = BackgroundToolLoopAsync(
                        chatMessages, chatOptions, firstResponse, tier,
                        message.SessionId, replyTo, correlationId, sessionCt);
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

                        _ = BackgroundToolLoopAsync(
                            chatMessages, chatOptions, firstResponse, tier,
                            message.SessionId, replyTo, correlationId, sessionCt);
                    }
                    else if (modelBehavior.NudgeOnHallucinatedToolCalls && HallucinatedActionRegex.IsMatch(text))
                    {
                        logger.LogWarning(
                            "Hallucinated tool actions detected on first response ({Length} chars); routing to background loop for nudge",
                            text.Length);

                        await PublishReplyAsync(
                            "I'm working on that — I'll follow up shortly.",
                            replyTo, correlationId, message.SessionId, isFinal: false, ct);

                        _ = BackgroundToolLoopAsync(
                            chatMessages, chatOptions, firstResponse, tier,
                            message.SessionId, replyTo, correlationId, sessionCt);
                    }
                    else
                    {
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
        CancellationToken ct)
    {
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
