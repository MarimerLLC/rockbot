using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.A2A;

/// <summary>
/// Handles <see cref="AgentTaskResult"/> messages from external agents.
/// Folds the result into the primary agent's LLM conversation.
///
/// For <see cref="AgentTaskState.InputRequired"/> results (queue transport),
/// keeps the pending task tracked, generates a trust-gated follow-up response
/// via <see cref="InputRequiredHandler"/>, and dispatches a follow-up
/// <see cref="AgentTaskRequest"/> with the same <c>ContextId</c>.
/// </summary>
#pragma warning disable CS9113 // Primary constructor parameters reserved for future handler expansion
internal sealed class A2ATaskResultHandler(
    AgentLoopRunner agentLoopRunner,
    AgentContextBuilder agentContextBuilder,
    ILlmClient llmClient,
    IMessagePublisher publisher,
    AgentIdentity agent,
    IWorkingMemory workingMemory,
    MemoryTools memoryTools,
    ISkillStore skillStore,
    IToolRegistry toolRegistry,
    RulesTools rulesTools,
    ToolGuideTools toolGuideTools,
    IConversationMemory conversationMemory,
    A2ATaskTracker tracker,
    ModelBehavior modelBehavior,
    AgentNameHolder agentNameHolder,
    InputRequiredHandler inputRequiredHandler,
    A2AOptions a2aOptions,
    SessionClientCapabilityStore clientCapabilityStore,
    ILogger<A2ATaskResultHandler> logger) : IMessageHandler<AgentTaskResult>
{
    private string DisplayName => agentNameHolder.DisplayName ?? agent.Name;

    /// <summary>
    /// True only when an A2A task originated from the primary agent's user session
    /// (e.g. "session/blazor-session"). Subagent sessions ("session/subagent-...") and
    /// transient executor sessions ("wisp-...") are not user-facing — A2A results
    /// directed at them must flow back to the calling loop via working memory rather
    /// than producing their own user-visible chat bubble. Only the primary agent
    /// communicates with the user.
    /// </summary>
    private static bool IsUserSession(string primarySessionId) =>
        primarySessionId.StartsWith("session/", StringComparison.OrdinalIgnoreCase) &&
        !primarySessionId.StartsWith("session/subagent-", StringComparison.OrdinalIgnoreCase);

    public async Task HandleAsync(AgentTaskResult result, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;
        var correlationId = context.Envelope.CorrelationId;

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            logger.LogDebug("Received AgentTaskResult with empty correlationId, ignoring");
            return;
        }

        // InputRequired: keep the task tracked for follow-up
        if (result.State == AgentTaskState.InputRequired)
        {
            await HandleInputRequiredAsync(result, correlationId, ct);
            return;
        }

        // Terminal states: remove and finalize
        if (!tracker.TryRemove(correlationId, out var pending) || pending is null)
        {
            logger.LogDebug("Received AgentTaskResult with correlationId={CorrelationId} — not tracked, ignoring", correlationId);
            return;
        }

        pending.Cts.Cancel();
        pending.Cts.Dispose();

        await HandleTerminalResultAsync(result, pending, ct);
    }

    private async Task HandleInputRequiredAsync(
        AgentTaskResult result, string correlationId, CancellationToken ct)
    {
        if (!tracker.TryGet(correlationId, out var pending) || pending is null)
        {
            logger.LogDebug(
                "Received InputRequired AgentTaskResult with correlationId={CorrelationId} — not tracked, ignoring",
                correlationId);
            return;
        }

        using var activity = A2ADiagnostics.Source.StartActivity("rockbot.a2a.input_required_loop");
        activity?.SetTag("rockbot.a2a.task_id", result.TaskId);
        activity?.SetTag("rockbot.a2a.context_id", result.ContextId ?? pending.ContextId);
        activity?.SetTag("rockbot.a2a.target_agent", pending.TargetAgent);
        activity?.SetTag("rockbot.a2a.session_id", pending.PrimarySessionId);
        activity?.SetTag("rockbot.a2a.correlation_id", correlationId);

        pending.InputRequiredRound++;
        pending.ContextId ??= result.ContextId;

        logger.LogInformation(
            "A2A task {TaskId} from '{TargetAgent}' requires input (round {Round})",
            result.TaskId, pending.TargetAgent, pending.InputRequiredRound);

        // Max rounds check
        if (pending.InputRequiredRound > a2aOptions.MaxInputRequiredRounds)
        {
            logger.LogWarning(
                "A2A task {TaskId} exceeded max InputRequired rounds ({Max})",
                result.TaskId, a2aOptions.MaxInputRequiredRounds);
            A2ADiagnostics.InputRequiredBreaks.Add(1,
                new KeyValuePair<string, object?>("rockbot.a2a.target_agent", pending.TargetAgent),
                new KeyValuePair<string, object?>("rockbot.a2a.reason", "max_rounds"));

            tracker.TryRemove(correlationId, out _);
            pending.Cts.Cancel();
            pending.Cts.Dispose();

            await PublishErrorToUserAsync(pending, result.TaskId,
                $"The multi-turn conversation with '{pending.TargetAgent}' was terminated after " +
                $"{a2aOptions.MaxInputRequiredRounds} rounds. Consider breaking the request into smaller parts.", ct);
            return;
        }

        var questionText = result.Message?.Parts
            .FirstOrDefault(p => p.Kind == "text")?.Text ?? "(no question)";

        // Get trust-gated follow-up response
        var followUp = await inputRequiredHandler.HandleAsync(
            new InputRequiredContext
            {
                TaskId = result.TaskId,
                ContextId = pending.ContextId,
                TargetAgent = pending.TargetAgent,
                Skill = pending.Skill,
                QuestionText = questionText,
                PrimarySessionId = pending.PrimarySessionId,
                Round = pending.InputRequiredRound
            }, ct);

        // Repetition detection
        if (pending.LastInputRequiredQuestion == questionText &&
            pending.LastInputRequiredAnswer == followUp.ResponseText)
        {
            // Simple consecutive-same detection — count via round tracking
            // For threshold-based detection, we check if we've seen the same Q/A
            // multiple times by comparing with the stored values.
            // The InputRequiredRepetitionDetector is used in the HTTP path;
            // here we do a simpler check since each round is a separate message.
        }
        pending.LastInputRequiredQuestion = questionText;
        pending.LastInputRequiredAnswer = followUp.ResponseText;

        logger.LogInformation(
            "A2A queue follow-up for task {TaskId} to '{TargetAgent}' (round {Round}, autonomous={Autonomous})",
            result.TaskId, pending.TargetAgent, pending.InputRequiredRound, followUp.WasAutonomous);

        activity?.SetTag("rockbot.a2a.round", pending.InputRequiredRound);
        activity?.SetTag("rockbot.a2a.autonomous", followUp.WasAutonomous);

        // Publish follow-up AgentTaskRequest with same contextId
        var followUpRequest = new AgentTaskRequest
        {
            TaskId = result.TaskId,
            ContextId = pending.ContextId,
            Skill = pending.Skill,
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = followUp.ResponseText }]
            }
        };

        var replyTo = $"{a2aOptions.CallerResultTopic}.{agent.Name}";
        var envelope = followUpRequest.ToEnvelope<AgentTaskRequest>(
            source: agent.Name,
            correlationId: correlationId,
            replyTo: replyTo);
        await publisher.PublishAsync(
            $"{a2aOptions.TaskTopic}.{pending.TargetAgent}", envelope, ct);
    }

    private async Task HandleTerminalResultAsync(
        AgentTaskResult result, PendingA2ATask pending, CancellationToken ct)
    {
        var a2aDurationMs = (DateTimeOffset.UtcNow - pending.StartedAt).TotalMilliseconds;
        A2ADiagnostics.Duration.Record(a2aDurationMs,
            new KeyValuePair<string, object?>("rockbot.a2a.target_agent", pending.TargetAgent),
            new KeyValuePair<string, object?>("rockbot.a2a.status", "ok"));

        logger.LogInformation(
            "A2A task result for task {TaskId} from agent '{TargetAgent}' in session {SessionId} (state={State})",
            result.TaskId, pending.TargetAgent, pending.PrimarySessionId, result.State);

        var resultText = result.Message?.Parts.FirstOrDefault(p => p.Kind == "text")?.Text ?? "(no text output)";
        var dataPart = result.Message?.Parts.FirstOrDefault(p => p.Kind == "data");
        var dataJson = dataPart?.Data;
        var dataMimeType = dataPart?.MimeType;
        string syntheticUserTurn;

        // PrimarySessionId is the full WM session namespace (e.g. "session/blazor-session"),
        // as populated by InvokeAgentExecutor from the RegistryToolFunction's sessionId parameter.
        // Use it directly as the namespace; derive the raw session ID by stripping the prefix so
        // AgentContextBuilder, conversation memory, and skill tools all use consistent keys.
        var sessionNamespace = pending.PrimarySessionId;
        const string SessionPrefix = "session/";
        var rawSessionId = sessionNamespace.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
            ? sessionNamespace[SessionPrefix.Length..]
            : sessionNamespace;

        // Purge any previous result entries for this agent before storing the new one.
        // Old entries linger in working memory (60-min TTL) and the LLM will find them
        // in conversation history instructions — causing it to retrieve stale data instead
        // of the current result when both share the same key pattern.
        var staleAgentPattern = $"/a2a/{pending.TargetAgent}/";
        var staleEntries = await workingMemory.ListAsync(sessionNamespace);
        foreach (var entry in staleEntries)
        {
            if (entry.Key.Contains(staleAgentPattern, StringComparison.OrdinalIgnoreCase) &&
                (entry.Key.EndsWith("/result", StringComparison.OrdinalIgnoreCase) ||
                 entry.Key.EndsWith("/result.data", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogDebug("Purging stale A2A result entry '{Key}' before storing new result", entry.Key);
                await workingMemory.DeleteAsync(entry.Key);
            }
        }

        // Always store A2A results in working memory so the LLM can reliably retrieve them
        // via get_from_working_memory regardless of result size. Storing inline in the
        // synthetic user turn caused the LLM to search WM, find nothing, and conclude the
        // result was unavailable — even though it was present in conversation history.
        var memoryKey = $"{sessionNamespace}/a2a/{pending.TargetAgent}/{result.TaskId}/result";
        await workingMemory.SetAsync(
            memoryKey,
            resultText,
            ttl: TimeSpan.FromMinutes(60),
            category: "a2a-result",
            tags: [pending.TargetAgent, result.TaskId]);

        logger.LogInformation(
            "A2A result for task {TaskId} ({Len:N0} chars) stored in working memory at key '{Key}'",
            result.TaskId, resultText.Length, memoryKey);

        // Multi-part responses (text + data) preserve the structured data part under a
        // sibling key so downstream consumers — the LLM's follow-up turn, subagent
        // callers, observability tooling — can recover the structured fields. Without
        // this, agents like AdvisorCouncil silently lose their JSON payload (per-persona
        // views, tensions, confidence, metadata) on the receive side.
        string? dataMemoryKey = null;
        if (!string.IsNullOrWhiteSpace(dataJson))
        {
            dataMemoryKey = $"{sessionNamespace}/a2a/{pending.TargetAgent}/{result.TaskId}/result.data";
            await workingMemory.SetAsync(
                dataMemoryKey,
                dataJson,
                ttl: TimeSpan.FromMinutes(60),
                category: "a2a-result-data",
                tags: [pending.TargetAgent, result.TaskId, dataMimeType ?? "application/json"]);

            logger.LogInformation(
                "A2A data part for task {TaskId} ({Len:N0} chars, mime={Mime}) stored at key '{Key}'",
                result.TaskId, dataJson.Length, dataMimeType ?? "application/json", dataMemoryKey);
        }

        // If the A2A invocation didn't originate in a user session, the result must flow
        // back to the calling loop (subagent or wisp) via working memory — not as a
        // user-visible chat bubble. The caller will pull the result and incorporate it
        // into its own output, which is the only thing the user should see. Skip the
        // synthesis + publish entirely for those callers.
        if (!IsUserSession(pending.PrimarySessionId))
        {
            logger.LogInformation(
                "A2A task {TaskId} originated from non-user session {SessionId} — skipping " +
                "synthesis and bubble publish (caller will consume the working-memory result)",
                result.TaskId, pending.PrimarySessionId);
            return;
        }

        syntheticUserTurn =
            $"[Agent '{pending.TargetAgent}' completed task {result.TaskId} (state={result.State})]: " +
            $"The result ({resultText.Length:N0} chars) is in working memory. " +
            $"Call get_from_working_memory with key '{memoryKey}' to read it before responding.";

        if (dataMemoryKey is not null)
        {
            syntheticUserTurn +=
                $" Structured JSON ({dataMimeType ?? "application/json"}) is also available " +
                $"at key '{dataMemoryKey}' — fetch it only if the structured fields are needed.";
        }

        // No separate preview bubble — the LLM synthesis below is the single
        // user-facing message. Publishing a preview AND a synthesis creates
        // duplicate confirmation messages in the UI.

        await conversationMemory.AddTurnAsync(
            rawSessionId,
            new ConversationTurn("user", syntheticUserTurn, DateTimeOffset.UtcNow)
            { AgentName = pending.TargetAgent },
            ct);

        var chatMessages = await agentContextBuilder.BuildAsync(
            rawSessionId, syntheticUserTurn, ct,
            clientCapabilities: clientCapabilityStore.Get(rawSessionId));

        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, sessionNamespace, logger);
        var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, rawSessionId);
        // Exclude A2A caller tools (invoke_agent, register_agent, etc.) from the result
        // synthesis — the LLM should present the result, not start new agent interactions.
        var a2aToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "invoke_agent", "register_agent", "unregister_agent", "list_known_agents", "get_agent_details" };
        var batchId = Guid.NewGuid().ToString("N")[..12];
        var registryTools = toolRegistry.BuildAgentToolFunctions(
            sessionNamespace, batchId, r => !a2aToolNames.Contains(r.Name));

        var chatOptions = new ChatOptions
        {
            Tools = [..memoryTools.Tools, ..sessionWorkingMemoryTools.Tools, ..sessionSkillTools.Tools,
                     ..rulesTools.Tools, ..toolGuideTools.Tools, ..registryTools]
        };

        try
        {
            using var progressCtx = ToolProgressNotifier.SetContext(new ToolProgressContext
            {
                SessionId = rawSessionId,
                AgentName = DisplayName,
                ReplyTo = $"{UserProxyTopics.UserResponse}.{agent.Name}"
            });

            var finalContent = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, rawSessionId,
                enableFollowUp: false, cancellationToken: ct);

            await conversationMemory.AddTurnAsync(
                rawSessionId,
                new ConversationTurn("assistant", finalContent, DateTimeOffset.UtcNow)
                { AgentName = DisplayName },
                ct);

            var reply = new AgentReply
            {
                Content = finalContent,
                SessionId = rawSessionId,
                AgentName = DisplayName,
                IsFinal = true
            };
            var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
            await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to handle A2A task result for task {TaskId}", result.TaskId);
        }
    }

    private async Task PublishErrorToUserAsync(
        PendingA2ATask pending, string taskId, string errorMessage, CancellationToken ct)
    {
        // Only the primary agent talks to the user. When the failed A2A invocation came
        // from a subagent or wisp, surface the error through that caller's normal output
        // path (it sees the failure via working memory / its loop's exception handling)
        // rather than emitting our own bubble.
        if (!IsUserSession(pending.PrimarySessionId))
        {
            logger.LogInformation(
                "Suppressing A2A error bubble for task {TaskId} — invocation came from non-user " +
                "session {SessionId}; caller will surface the error in its own output",
                taskId, pending.PrimarySessionId);
            return;
        }

        var sessionNamespace = pending.PrimarySessionId;
        const string SessionPrefix = "session/";
        var rawSessionId = sessionNamespace.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
            ? sessionNamespace[SessionPrefix.Length..]
            : sessionNamespace;

        try
        {
            var reply = new AgentReply
            {
                Content = errorMessage,
                SessionId = rawSessionId,
                AgentName = DisplayName,
                IsFinal = true
            };
            var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
            await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to publish InputRequired error for task {TaskId}", taskId);
        }
    }
}
