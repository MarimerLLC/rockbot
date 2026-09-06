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
#pragma warning disable CS9113 // Primary constructor parameters reserved for future handler expansion
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
    ISessionTracker sessionTracker,
    SessionStartTracker sessionStartTracker,
    SessionClientCapabilityStore clientCapabilityStore,
    ReplyAttachmentBuffer attachmentBuffer,
    McpBridge.Attachments.IAttachmentStorage attachmentStorage,
    SessionOriginStore originStore,
    IOptions<AgentProfileOptions> profileOptions,
    IOptions<AgentHostOptions> hostOptions,
    IWipTracker wipTracker,
    AgentNameHolder agentNameHolder,
    ILogger<UserMessageHandler> logger,
    TierRoutingLogger tierRoutingLogger,
    ISkillUsageStore? skillUsageStore = null) : IMessageHandler<UserMessage>
{
    private static readonly TimeSpan ProgressMessageThreshold = TimeSpan.FromSeconds(5);

    // Heuristic for "thread is currently active" — controls whether the tier selector
    // applies the short-message-on-active-thread override. Conservative enough to leave
    // first-turn and stale-session messages on the existing routing path. See #383.
    // Shared with AgentContextBuilder's recent-window query enrichment (#397) so both
    // read "the thread is live" the same way.
    private const int ThreadEstablishedMinTurns = ShortMessageHeuristics.ThreadEstablishedMinTurns;
    private static readonly TimeSpan ThreadEstablishedRecency = ShortMessageHeuristics.ThreadEstablishedRecency;

    // Shared with AgentLoopRunner — single source of truth for hallucinated-action detection.
    private static readonly Regex HallucinatedActionRegex = AgentLoopRunner.HallucinatedActionRegex;

    private static readonly Regex CorrectionRegex = new(
        @"\b(no[,\s]|that'?s?\s+(wrong|incorrect|not right)|you were wrong|actually[,\s]|that didn'?t work|try again)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task HandleAsync(UserMessage message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo ?? $"{UserProxyTopics.UserResponse}.{agent.Name}";
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;
        var wipMessageId = context.Items.TryGetValue(WipConstants.MessageIdKey, out var id)
            ? (string)id : null;

        userActivityMonitor.RecordActivity();

        // Cache the client's advertised rendering capabilities so other entry points
        // producing replies for this session (A2A handlers, subagent runner) can honour
        // them without the originating UserMessage in scope. Last-writer-wins handles
        // a user switching clients mid-conversation.
        clientCapabilityStore.Set(message.SessionId, message.ClientCapabilities);

        // Record the session origin (channel + first-prompt summary + start time) once, so
        // unsolicited replies produced later for this session (subagent/A2A/scheduled
        // completions) can anchor themselves to the request that started the work. First
        // writer wins — set inside the store — so follow-up turns keep the original anchor.
        var channel = !string.IsNullOrWhiteSpace(message.ChannelName)
            ? message.ChannelName!
            : ChannelFromSource(context.Envelope.Source);
        originStore.Set(message.SessionId, new ReplyOrigin(
            Channel: channel,
            PromptSummary: SummarizePrompt(message.Content),
            StartedAt: DateTimeOffset.UtcNow,
            SessionId: message.SessionId));

        // Cancel any background loop still running for this session from a prior message.
        // This prevents stale tool calls (e.g. sending an email from a previous topic)
        // from executing after the user has already moved on.
        var sessionHandle = sessionTracker.BeginSession(message.SessionId, ct);
        var sessionCt = sessionHandle.Token;
        logger.LogInformation("Received message from {UserId} in session {SessionId}: {Content}",
            message.UserId, message.SessionId, message.Content);

        // Establish whether this session already has an active topical thread, so
        // the tier selector can route short follow-ups through Balanced instead of
        // Low. Read prior turns before AddTurnAsync runs (further below) so the
        // count reflects history, not the incoming message itself. See issue #383.
        var priorTurns = await conversationMemory.GetTurnsAsync(message.SessionId, ct);
        var threadEstablished = priorTurns.Count >= ThreadEstablishedMinTurns
            && (DateTimeOffset.UtcNow - priorTurns[^1].Timestamp) <= ThreadEstablishedRecency;

        var classification = tierSelector.Classify(
            message.Content,
            new TierRoutingContext(Origin: "user-message", ThreadEstablished: threadEstablished));
        var tier = classification.Tier;
        logger.LogInformation(
            "Routing user message to tier={Tier} (score={Score:F3}, threadEstablished={ThreadEstablished})",
            tier, classification.ComplexityScore, threadEstablished);
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
            var chatMessages = await agentContextBuilder.BuildAsync(
                message.SessionId, message.Content, ct,
                clientCapabilities: message.ClientCapabilities);
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

            // Per-turn attachment tool — attach_image stages files for this turn's final reply.
            var attachmentReplyTools = new AttachmentReplyTools(
                attachmentStorage, attachmentBuffer, message.SessionId, turnId, logger);

            // Per-session skill tools with usage tracking
            var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, message.SessionId, skillUsageStore);

            var batchId = Guid.NewGuid().ToString("N")[..12];
            var registryTools = toolRegistry.BuildAgentToolFunctions(
                sessionNamespace, batchId, ToolProfiles.Main, logger: logger);

            var allTools = memoryTools.Tools
                .Concat(sessionWorkingMemoryTools.Tools)
                .Concat(attachmentReplyTools.Tools)
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
                    replyTo, correlationId, message.SessionId, turnId, isFinal: false, ct);
                turnActivityHandedOff = true;
                context.Items[WipConstants.DeferredKey] = true;
                _ = NativeLlmLoopAsync(chatMessages, chatOptions, classification, message.Content, postInjectionTokenEstimate,
                    message.SessionId, turnId, replyTo, correlationId, sessionHandle.Generation, wipMessageId, turnActivity, sessionCt);
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

                // Routing telemetry is written at a terminal point (BackgroundToolLoopAsync
                // after the loop, or the single-response branch below) so the entry carries
                // the multi-iteration aggregate token usage rather than just iteration 1.

                if (hasToolCalls)
                {
                    var effectiveAck = string.IsNullOrWhiteSpace(ackText)
                        ? "I'm working on that — I'll follow up shortly."
                        : ackText;

                    logger.LogInformation(
                        "Tool calls detected on iteration 1; sending ack ({AckLen} chars) and continuing in background",
                        effectiveAck.Length);

                    await PublishReplyAsync(effectiveAck, replyTo, correlationId, message.SessionId, turnId, isFinal: false, ct);

                    turnActivityHandedOff = true;
                    context.Items[WipConstants.DeferredKey] = true;
                    _ = BackgroundToolLoopAsync(
                        chatMessages, chatOptions, firstResponse, classification, message.Content, postInjectionTokenEstimate,
                        message.SessionId, turnId, replyTo, correlationId, sessionHandle.Generation, wipMessageId, turnActivity, sessionCt);
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
                            replyTo, correlationId, message.SessionId, turnId, isFinal: false, ct);

                        turnActivityHandedOff = true;
                        context.Items[WipConstants.DeferredKey] = true;
                        _ = BackgroundToolLoopAsync(
                            chatMessages, chatOptions, firstResponse, classification, message.Content, postInjectionTokenEstimate,
                            message.SessionId, turnId, replyTo, correlationId, sessionHandle.Generation, wipMessageId, turnActivity, sessionCt);
                    }
                    else if (modelBehavior.NudgeOnHallucinatedToolCalls
                        && (HallucinatedActionRegex.IsMatch(text) || AgentLoopRunner.CapabilityDenialRegex.IsMatch(text)))
                    {
                        logger.LogWarning(
                            "Hallucinated action or capability denial on first response ({Length} chars); routing to background loop for nudge",
                            text.Length);

                        await PublishReplyAsync(
                            "I'm working on that — I'll follow up shortly.",
                            replyTo, correlationId, message.SessionId, turnId, isFinal: false, ct);

                        turnActivityHandedOff = true;
                        context.Items[WipConstants.DeferredKey] = true;
                        _ = BackgroundToolLoopAsync(
                            chatMessages, chatOptions, firstResponse, classification, message.Content, postInjectionTokenEstimate,
                            message.SessionId, turnId, replyTo, correlationId, sessionHandle.Generation, wipMessageId, turnActivity, sessionCt);
                    }
                    else
                    {
                        // Single-response text path (no background loop): firstResponse IS
                        // the complete response, so its usage is the full per-turn aggregate.
                        _ = tierRoutingLogger.AppendAsync(new TierRoutingEntry
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            PromptPreview = ToPromptPreview(message.Content),
                            Tier = tier,
                            Context = "user-message",
                            ComplexityScore = classification.ComplexityScore,
                            MatchedHighKeywords = classification.MatchedHighKeywords,
                            MatchedLowKeywords = classification.MatchedLowKeywords,
                            PostInjectionTokenEstimate = postInjectionTokenEstimate,
                            ModelId = firstResponse.ModelId ?? registry.GetModelId(tier),
                            InputTokens = firstResponse.Usage?.InputTokenCount,
                            OutputTokens = firstResponse.Usage?.OutputTokenCount,
                            LatencyMs = routingSw.ElapsedMilliseconds,
                        });

                        await conversationMemory.AddTurnAsync(
                            message.SessionId,
                            new ConversationTurn("assistant", text, DateTimeOffset.UtcNow)
                            { AgentName = agent.Name },
                            ct);

                        await PublishReplyAsync(text, replyTo, correlationId, message.SessionId, turnId, isFinal: true, ct);
                        sessionTracker.EndSession(message.SessionId, sessionHandle.Generation);
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

            if (hostOptions.Value.PersistErrorTurns)
            {
                try
                {
                    await conversationMemory.AddTurnAsync(
                        message.SessionId,
                        new ConversationTurn("assistant", errorText, DateTimeOffset.UtcNow)
                        { AgentName = agent.Name },
                        CancellationToken.None);
                }
                catch (Exception memEx)
                {
                    logger.LogWarning(memEx, "Failed to record error assistant turn for session {SessionId}",
                        message.SessionId);
                }
            }

            await PublishReplyAsync(errorText, replyTo, correlationId, message.SessionId, turnId, isFinal: true, ct);
            sessionTracker.EndSession(message.SessionId, sessionHandle.Generation);
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
            {
                // Clear any attachments staged this turn that were never drained onto a final
                // reply (e.g. the turn threw before publishing). Idempotent: a no-op after a
                // successful final drain. Skipped when handed off — the background method owns
                // the turn's stage and clears it in its own finally.
                attachmentBuffer.Clear(message.SessionId, turnId);
                turnActivity?.Dispose();
            }
        }
    }

    private async Task NativeLlmLoopAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        TierClassification classification,
        string userPrompt,
        int? postInjectionTokenEstimate,
        string sessionId,
        string turnId,
        string replyTo,
        string? correlationId,
        long sessionGeneration,
        string? wipMessageId,
        System.Diagnostics.Activity? turnActivity,
        CancellationToken ct)
    {
        var loopSw = System.Diagnostics.Stopwatch.StartNew();
        var nativeTierTag = new KeyValuePair<string, object?>("rockbot.llm.tier", classification.Tier.ToString());

        // Subscribe to fallback events so the user sees model switches in the UI.
        var fallbackClient = registry.GetClient(classification.Tier)
            .GetService<FallbackChatClient>();
        void OnFallback(string from, string to, string reason) =>
            _ = PublishReplyAsync(
                $"Switching models ({reason}) — retrying with {to}…",
                replyTo, correlationId, sessionId, turnId, isFinal: false, ct);
        if (fallbackClient is not null) fallbackClient.OnFallback += OnFallback;

        try
        {
            await using var slot = await workSerializer.AcquireForUserAsync(ct);

            logger.LogInformation("Native LLM loop started for session {SessionId}", sessionId);

            using var progressCtx = ToolProgressNotifier.SetContext(new ToolProgressContext
            {
                SessionId = sessionId,
                AgentName = agent.Name,
                CorrelationId = correlationId,
                ReplyTo = replyTo
            });

            var lastProgressAt = DateTimeOffset.UtcNow;
            var nativeDiag = new LoopDiagnostics();

            var text = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, sessionId, tier: classification.Tier,
                complexityScore: classification.ComplexityScore,
                diagnostics: nativeDiag,
                onPreToolCall: async (desc, ct2) =>
                {
                    await PublishReplyAsync($"Working on it — checking {desc}…", replyTo, correlationId, sessionId, turnId, isFinal: false, ct2);
                    lastProgressAt = DateTimeOffset.UtcNow;
                },
                onProgress: async (msg, ct2) =>
                {
                    if (DateTimeOffset.UtcNow - lastProgressAt < ProgressMessageThreshold)
                        return;
                    await PublishReplyAsync(msg, replyTo, correlationId, sessionId, turnId, isFinal: false, ct2);
                    lastProgressAt = DateTimeOffset.UtcNow;
                },
                onToolTimeout: async (desc, ct2) =>
                {
                    await PublishReplyAsync(
                        $"The {desc} service is taking too long to respond — trying a different approach…",
                        replyTo, correlationId, sessionId, turnId, isFinal: false, ct2);
                    lastProgressAt = DateTimeOffset.UtcNow;
                },
                onStageProgress: async (stage, ct2) =>
                {
                    await PublishReplyAsync(stage, replyTo, correlationId, sessionId, turnId, isFinal: false, ct2);
                    lastProgressAt = DateTimeOffset.UtcNow;
                },
                cancellationToken: ct);

            logger.LogInformation(
                "Native path complete — final text {TextLen} chars", text.Length);

            _ = tierRoutingLogger.AppendAsync(new TierRoutingEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                PromptPreview = ToPromptPreview(userPrompt),
                Tier = classification.Tier,
                Context = "user-message",
                ComplexityScore = classification.ComplexityScore,
                MatchedHighKeywords = classification.MatchedHighKeywords,
                MatchedLowKeywords = classification.MatchedLowKeywords,
                PostInjectionTokenEstimate = postInjectionTokenEstimate,
                ModelId = nativeDiag.ModelId ?? registry.GetModelId(classification.Tier),
                InputTokens = nativeDiag.InputTokens > 0 ? nativeDiag.InputTokens : null,
                OutputTokens = nativeDiag.OutputTokens > 0 ? nativeDiag.OutputTokens : null,
                ToolCallCount = nativeDiag.ToolCalls > 0 ? nativeDiag.ToolCalls : null,
            });

            text = ResponseSanitizer.StripTrailingOffers(text);

            await conversationMemory.AddTurnAsync(
                sessionId,
                new ConversationTurn("assistant", text, DateTimeOffset.UtcNow)
                { AgentName = agent.Name },
                ct);

            // The parent reply is the user's "I'm starting work" / direct answer bubble
            // and resolves the user-proxy SendAsync TCS. If the loop spawned consolidating
            // subagents, their Phase 2 synthesis will arrive later as a separate
            // unsolicited final bubble — that's the consolidated answer.
            await PublishReplyAsync(text, replyTo, correlationId, sessionId, turnId, isFinal: true, ct);
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
                replyTo, correlationId, sessionId, turnId, isFinal: true, ct);
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
            if (fallbackClient is not null) fallbackClient.OnFallback -= OnFallback;
            // Clear any attachments staged this turn that were never drained — on a clean final
            // reply this is a no-op; on cancellation (user sent a new message mid-loop) it removes
            // the orphaned stage so it can't land on a later turn's reply.
            attachmentBuffer.Clear(sessionId, turnId);
            sessionTracker.EndSession(sessionId, sessionGeneration);
            if (wipMessageId is not null)
                await wipTracker.CompleteAsync(wipMessageId, CancellationToken.None);
            turnActivity?.Dispose();
        }
    }

    private async Task BackgroundToolLoopAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        ChatResponse firstResponse,
        TierClassification classification,
        string userPrompt,
        int? postInjectionTokenEstimate,
        string sessionId,
        string turnId,
        string replyTo,
        string? correlationId,
        long sessionGeneration,
        string? wipMessageId,
        System.Diagnostics.Activity? turnActivity,
        CancellationToken ct)
    {
        var tier = classification.Tier;
        var loopSw = System.Diagnostics.Stopwatch.StartNew();
        var bgTierTag = new KeyValuePair<string, object?>("rockbot.llm.tier", tier.ToString());
        var bgDiag = new LoopDiagnostics();

        // Subscribe to fallback events so the user sees model switches in the UI.
        var fallbackClient = registry.GetClient(tier)
            .GetService<FallbackChatClient>();
        void OnFallback(string from, string to, string reason) =>
            _ = PublishReplyAsync(
                $"Switching models ({reason}) — retrying with {to}…",
                replyTo, correlationId, sessionId, turnId, isFinal: false, ct);
        if (fallbackClient is not null) fallbackClient.OnFallback += OnFallback;

        try
        {
            // Acquire the single execution slot, preempting any running scheduled
            // task. If this session itself is cancelled (new user message) while
            // waiting, the await throws OperationCanceledException and we exit.
            await using var slot = await workSerializer.AcquireForUserAsync(ct);

            logger.LogInformation("Background tool loop started for session {SessionId}", sessionId);

            using var progressCtx = ToolProgressNotifier.SetContext(new ToolProgressContext
            {
                SessionId = sessionId,
                AgentName = agent.Name,
                CorrelationId = correlationId,
                ReplyTo = replyTo
            });

            var lastProgressAt = DateTimeOffset.UtcNow;

            var finalContent = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, sessionId, firstResponse: firstResponse, tier: tier,
                complexityScore: classification.ComplexityScore,
                diagnostics: bgDiag,
                onPreToolCall: async (desc, ct2) =>
                {
                    await PublishReplyAsync($"Working on it — checking {desc}…", replyTo, correlationId, sessionId, turnId, isFinal: false, ct2);
                    lastProgressAt = DateTimeOffset.UtcNow;
                },
                onProgress: async (msg, ct2) =>
                {
                    if (DateTimeOffset.UtcNow - lastProgressAt < ProgressMessageThreshold)
                        return;
                    await PublishReplyAsync(msg, replyTo, correlationId, sessionId, turnId, isFinal: false, ct2);
                    lastProgressAt = DateTimeOffset.UtcNow;
                },
                onToolTimeout: async (desc, ct2) =>
                {
                    await PublishReplyAsync(
                        $"The {desc} service is taking too long to respond — trying a different approach…",
                        replyTo, correlationId, sessionId, turnId, isFinal: false, ct2);
                    lastProgressAt = DateTimeOffset.UtcNow;
                },
                onStageProgress: async (stage, ct2) =>
                {
                    await PublishReplyAsync(stage, replyTo, correlationId, sessionId, turnId, isFinal: false, ct2);
                    lastProgressAt = DateTimeOffset.UtcNow;
                },
                cancellationToken: ct);

            // Routing telemetry written here (terminal point) so the entry carries the
            // multi-iteration aggregate token usage accumulated across the whole loop.
            _ = tierRoutingLogger.AppendAsync(new TierRoutingEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                PromptPreview = ToPromptPreview(userPrompt),
                Tier = tier,
                Context = "user-message",
                ComplexityScore = classification.ComplexityScore,
                MatchedHighKeywords = classification.MatchedHighKeywords,
                MatchedLowKeywords = classification.MatchedLowKeywords,
                PostInjectionTokenEstimate = postInjectionTokenEstimate,
                ModelId = bgDiag.ModelId ?? registry.GetModelId(tier),
                InputTokens = bgDiag.InputTokens > 0 ? bgDiag.InputTokens : null,
                OutputTokens = bgDiag.OutputTokens > 0 ? bgDiag.OutputTokens : null,
                ToolCallCount = bgDiag.ToolCalls > 0 ? bgDiag.ToolCalls : null,
            });

            await conversationMemory.AddTurnAsync(
                sessionId,
                new ConversationTurn("assistant", finalContent, DateTimeOffset.UtcNow)
                { AgentName = agent.Name },
                ct);

            await PublishReplyAsync(finalContent, replyTo, correlationId, sessionId, turnId, isFinal: true, ct);
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
                replyTo, correlationId, sessionId, turnId, isFinal: true, ct);
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
            if (fallbackClient is not null) fallbackClient.OnFallback -= OnFallback;
            // Clear any attachments staged this turn that were never drained — on a clean final
            // reply this is a no-op; on cancellation (user sent a new message mid-loop) it removes
            // the orphaned stage so it can't land on a later turn's reply.
            attachmentBuffer.Clear(sessionId, turnId);
            sessionTracker.EndSession(sessionId, sessionGeneration);
            if (wipMessageId is not null)
                await wipTracker.CompleteAsync(wipMessageId, CancellationToken.None);
            turnActivity?.Dispose();
        }
    }

    private async Task PublishReplyAsync(
        string content, string replyTo, string? correlationId,
        string sessionId, string turnId, bool isFinal, CancellationToken ct)
    {
        // Only final replies carry attachments — drain this turn's stage so files staged by
        // attach_image ride out with the answer and aren't replayed on a later turn. The shared
        // helper returns null for non-final replies and when nothing is staged.
        var attachments = attachmentBuffer.DrainForFinalReply(sessionId, turnId, isFinal);

        var reply = new AgentReply
        {
            Content = content,
            SessionId = sessionId,
            AgentName = agentNameHolder.DisplayName ?? agent.Name,
            IsFinal = isFinal,
            Attachments = attachments
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

    /// <summary>
    /// Truncates the raw user prompt for the routing log's <c>PromptPreview</c> field.
    /// Must be given the prompt for the current turn — deriving it from the assembled
    /// <c>chatMessages</c> yields the oldest user turn in the window instead (issue #556).
    /// </summary>
    internal static string ToPromptPreview(string content) =>
        content.Length > 150 ? content[..150] : content;

    /// <summary>
    /// Truncates the user's prompt to a short single-line summary for the origin anchor.
    /// Cheap and synchronous so it adds no latency at the message entry point.
    /// </summary>
    private static string SummarizePrompt(string content)
    {
        const int MaxLength = 80;
        var trimmed = content.Trim().ReplaceLineEndings(" ");
        return trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength].TrimEnd() + "…";
    }

    /// <summary>
    /// Derives a channel name from the envelope source (proxy id), used only when the inbound
    /// message did not carry an explicit <see cref="UserMessage.ChannelName"/>. A proxy id like
    /// "cli-rocky-abc123" yields "cli".
    /// </summary>
    private static string ChannelFromSource(string? source) =>
        string.IsNullOrWhiteSpace(source) ? "unknown" : source.Split('-', 2)[0];
}
