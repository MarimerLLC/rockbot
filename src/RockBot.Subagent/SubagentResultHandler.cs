using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.Subagent;

/// <summary>
/// Handles subagent result messages on the primary agent side.
/// Uses a two-phase approach: Phase 1 records the result immediately,
/// Phase 2 runs consolidated synthesis only when all sibling results are in.
/// </summary>
internal sealed class SubagentResultHandler(
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
    SubagentResultGate gate,
    ISubagentManager subagentManager,
    ISessionTracker sessionTracker,
    ILogger<SubagentResultHandler> logger) : IMessageHandler<SubagentResultMessage>
{
    public async Task HandleAsync(SubagentResultMessage message, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;

        logger.LogInformation(
            "Subagent result for task {TaskId} in primary session {SessionId}: success={Success}, output={OutputLen} chars, batchId={BatchId}, consolidate={Consolidate}",
            message.TaskId, message.PrimarySessionId, message.IsSuccess, message.Output.Length,
            message.BatchId, message.Consolidate);

        if (string.IsNullOrWhiteSpace(message.Output))
            logger.LogWarning("Subagent {TaskId} returned empty output — primary agent will have nothing to relay", message.TaskId);

        // PrimarySessionId is the full WM session namespace (e.g. "session/blazor-session"),
        // as populated by SpawnSubagentExecutor from the RegistryToolFunction's sessionId parameter.
        // Derive the raw session ID by stripping the prefix so conversation memory, context builder,
        // and skill tools all use the same key as UserMessageHandler.
        var sessionNamespace = message.PrimarySessionId;
        const string SessionPrefix = "session/";
        var rawSessionId = sessionNamespace.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
            ? sessionNamespace[SessionPrefix.Length..]
            : sessionNamespace;

        // ── Phase 1: immediate per-result work (every result) ──────────────────

        var safeOutput = message.IsSuccess && AgentLoopRunner.IsIncompleteSetupPhrase(message.Output)
            ? message.Output.TrimEnd(':').TrimEnd() +
              " — but the task ran out of steps before completing this action. No data was saved to shared memory."
            : message.Output;

        // Publish completion bubble to UI (non-final)
        try
        {
            var completionContent = message.IsSuccess
                ? safeOutput
                : $"Task failed: {message.Error}\n\n{message.Output}";
            var completionReply = new AgentReply
            {
                Content = completionContent,
                SessionId = rawSessionId,
                AgentName = $"subagent-{message.TaskId}",
                IsFinal = false,
                IsCompletion = true
            };
            var completionEnvelope = completionReply.ToEnvelope<AgentReply>(
                source: $"subagent-{message.TaskId}");
            await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", completionEnvelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish completion bubble for subagent {TaskId}", message.TaskId);
        }

        // Build whiteboard hint for this result
        var subagentPrefix = $"subagent/{message.TaskId}/";
        var whiteboardEntries = await workingMemory.ListAsync(subagentPrefix);

        var whiteboardHint = whiteboardEntries.Count > 0
            ? $" The subagent stored {whiteboardEntries.Count} output(s) in working memory under namespace '{subagentPrefix.TrimEnd('/')}'. " +
              $"Keys: {string.Join(", ", whiteboardEntries.Select(e => $"'{e.Key}'"))}. " +
              "Retrieve and present them to the user using get_from_working_memory with the full key."
            : string.Empty;

        // Add synthetic user turn to conversation memory
        var syntheticUserTurn = message.IsSuccess
            ? $"[Subagent task {message.TaskId} completed]: {safeOutput}{whiteboardHint}"
            : $"[Subagent task {message.TaskId} completed with error: {message.Error}]: {message.Output}";

        await conversationMemory.AddTurnAsync(
            rawSessionId,
            new ConversationTurn("user", syntheticUserTurn, DateTimeOffset.UtcNow)
            { AgentName = $"subagent-{message.TaskId}" },
            ct);

        // ── Gate: accumulate and decide who synthesizes ─────────────────────────

        var batchedResults = await gate.AccumulateAsync(message, subagentManager, ct);

        if (batchedResults is null)
        {
            // Another handler invocation will perform consolidated synthesis
            logger.LogInformation(
                "Subagent result {TaskId} deferred to batch consolidation winner", message.TaskId);
            return;
        }

        // ── Phase 2: consolidated synthesis (winner only) ───────────────────────

        logger.LogInformation(
            "Running consolidated synthesis for {Count} result(s) in session {SessionId}",
            batchedResults.Count, rawSessionId);

        // For multi-result batches, collect whiteboard hints for ALL results
        // and add a final synthetic turn requesting consolidation
        if (batchedResults.Count > 1)
        {
            var allHints = new List<string>();
            foreach (var r in batchedResults.Where(r => r.TaskId != message.TaskId))
            {
                var prefix = $"subagent/{r.TaskId}/";
                var entries = await workingMemory.ListAsync(prefix);
                if (entries.Count > 0)
                {
                    allHints.Add(
                        $"Subagent {r.TaskId} stored {entries.Count} output(s) under '{prefix.TrimEnd('/')}': " +
                        string.Join(", ", entries.Select(e => $"'{e.Key}'")));
                }
            }

            var consolidationNote = allHints.Count > 0
                ? " " + string.Join(" ", allHints)
                : string.Empty;

            var consolidationTurn =
                $"[All {batchedResults.Count} subagent tasks completed. " +
                $"Synthesize the results above into a single unified response.]{consolidationNote}";

            await conversationMemory.AddTurnAsync(
                rawSessionId,
                new ConversationTurn("system", consolidationTurn, DateTimeOffset.UtcNow),
                ct);
        }

        var chatMessages = await agentContextBuilder.BuildAsync(
            rawSessionId,
            batchedResults.Count > 1
                ? $"[Consolidating {batchedResults.Count} subagent results]"
                : syntheticUserTurn,
            ct);

        // Detect whether the user has already sent a new message and moved on.
        if (sessionTracker.HasActiveUserLoop(rawSessionId))
        {
            logger.LogInformation(
                "User has an active message loop for session {BaseSessionId} — adding background-work framing to synthesis",
                rawSessionId);

            chatMessages.Add(new ChatMessage(ChatRole.System,
                "IMPORTANT: The user has sent a new message and moved on to a different topic while this " +
                "background task was running. Frame your response as a brief follow-up delivering results " +
                "from earlier background work — e.g. 'Circling back on the earlier [topic] research…' " +
                "Keep it concise and do not address or repeat anything related to the user's current question."));
        }

        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, sessionNamespace, logger);
        var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, rawSessionId);
        var registryTools = toolRegistry.GetTools()
            .Select(r => (AIFunction)new SubagentRegistryToolFunction(
                r, toolRegistry.GetExecutor(r.Name)!, sessionNamespace))
            .ToArray();

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
                AgentName = agent.Name,
                ReplyTo = $"{UserProxyTopics.UserResponse}.{agent.Name}"
            });

            var finalContent = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, rawSessionId,
                enableFollowUp: false, cancellationToken: ct);

            await conversationMemory.AddTurnAsync(
                rawSessionId,
                new ConversationTurn("assistant", finalContent, DateTimeOffset.UtcNow)
                { AgentName = agent.Name },
                ct);

            // The synthesis is the consolidated answer bubble. The parent loop already
            // emitted its own final reply (with the original user correlationId) before
            // spawning subagents, so this synthesis arrives as an unsolicited final
            // reply and renders as a separate chat bubble in the UI — the user sees
            // the parent's "I'll delegate…" announcement followed by this consolidated
            // answer once all subagents complete.
            var reply = new AgentReply
            {
                Content = finalContent,
                SessionId = rawSessionId,
                AgentName = agent.Name,
                IsFinal = true
            };
            var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
            await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Failed to handle subagent result consolidation for session {SessionId} — emitting degraded reply",
                rawSessionId);

            await PublishDegradedReplyAsync(rawSessionId, batchedResults, ex, ct);
        }
        // Note: subagent working memory entries ("subagent/{taskId}/...") are intentionally NOT
        // deleted here. They persist until their TTL expires so the primary agent can reference
        // them across multiple follow-up turns.
    }

    /// <summary>
    /// Synthesis LLM call failed (e.g. all providers 503'd). The per-subagent outputs are still
    /// in working memory and conversation memory — stitch them into a plain reply so the user
    /// receives the work that did succeed instead of silent failure.
    /// </summary>
    private async Task PublishDegradedReplyAsync(
        string rawSessionId,
        IReadOnlyList<SubagentResultMessage> batchedResults,
        Exception synthesisError,
        CancellationToken ct)
    {
        try
        {
            var content = BuildDegradedReplyContent(batchedResults, synthesisError);

            await conversationMemory.AddTurnAsync(
                rawSessionId,
                new ConversationTurn("assistant", content, DateTimeOffset.UtcNow)
                { AgentName = agent.Name },
                ct);

            var reply = new AgentReply
            {
                Content = content,
                SessionId = rawSessionId,
                AgentName = agent.Name,
                IsFinal = true
            };
            var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
            await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Degraded subagent consolidation reply also failed for session {SessionId}",
                rawSessionId);
        }
    }

    internal static string BuildDegradedReplyContent(
        IReadOnlyList<SubagentResultMessage> batchedResults,
        Exception synthesisError)
    {
        var successCount = batchedResults.Count(r => r.IsSuccess);
        var failCount = batchedResults.Count - successCount;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(
            $"I couldn't synthesize a unified summary — the synthesis step itself failed ({synthesisError.Message.Trim()}). " +
            $"Here are the raw subagent results ({successCount} succeeded, {failCount} failed):");
        sb.AppendLine();

        foreach (var r in batchedResults)
        {
            sb.AppendLine($"### Subagent {r.TaskId} — {(r.IsSuccess ? "succeeded" : "failed")}");
            if (!r.IsSuccess && !string.IsNullOrWhiteSpace(r.Error))
                sb.AppendLine($"_Error: {r.Error}_");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(r.Output) ? "_(no output)_" : r.Output);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
