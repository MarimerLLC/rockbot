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
/// Handles <see cref="ScheduledTaskMessage"/> by invoking the LLM with the full agent
/// tool set and publishing the result as an <see cref="AgentReply"/>.
/// </summary>
internal sealed class ScheduledTaskHandler(
    ILlmClient llmClient,
    IMessagePublisher publisher,
    AgentIdentity agent,
    IToolRegistry toolRegistry,
    RulesTools rulesTools,
    MemoryTools memoryTools,
    IWorkingMemory workingMemory,
    ISkillStore skillStore,
    ToolGuideTools toolGuideTools,
    ModelBehavior modelBehavior,
    AgentLoopRunner agentLoopRunner,
    AgentContextBuilder agentContextBuilder,
    IOptions<AgentProfileOptions> profileOptions,
    IScheduledTaskStore scheduledTaskStore,
    ILogger<ScheduledTaskHandler> logger,
    ISkillUsageStore? skillUsageStore = null,
    IMcpSkillSurface? mcpSkillSurface = null,
    IOptions<WorkingMemoryOptions>? workingMemoryOptions = null,
    ResultArchive? resultArchive = null,
    IConversationMemory? conversationMemory = null,
    ScheduledTaskRunCosts? runCosts = null,
    ISavedWispRunner? savedWispRunner = null,
    TimeProvider? timeProvider = null) : IMessageHandler<ScheduledTaskMessage>
{
    /// <summary>How much of a gate wisp's output is put in the patrol turn's context.</summary>
    internal const int MaxGateOutputChars = 12_000;

    public async Task HandleAsync(ScheduledTaskMessage message, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;
        var sessionId = $"{WellKnownSessions.ScheduledTaskPrefix}{message.TaskName}";
        logger.LogInformation("Executing scheduled task '{TaskName}'", message.TaskName);

        var taskRecord = await scheduledTaskStore.GetAsync(message.TaskName);

        // #846: the patrol turn runs on Balanced unless the task carries a lower ceiling.
        var turnTier = taskRecord?.Cap(ModelTier.Balanced) ?? ModelTier.Balanced;
        if (taskRecord?.MaxTier is { } maxTier)
            logger.LogInformation("Scheduled task '{Task}' is capped at {MaxTier}: {Reason}",
                message.TaskName, maxTier, taskRecord.MaxTierReason);
        runCosts?.BeginRun(message.TaskName, turnTier);
        var runStartedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();

        // #848: the run's read-only fetches, shared by the gate wisp and the patrol turn, so the
        // turn is told when it re-fetches what the wisp already has; its repeat count is recorded.
        var fetchRegistry = new RunFetchRegistry();

        // #848: a gate wisp runs before any LLM work. When it finds nothing new the run ends here.
        GateRun? gateRun = null;
        if (taskRecord?.Gate is { } gate)
        {
            gateRun = await RunGateAsync(taskRecord, gate, sessionId, fetchRegistry, ct);
            runCosts?.NoteGate(message.TaskName, gateRun.Result);
            if (!gateRun.RunLlm)
            {
                logger.LogInformation("Scheduled task '{TaskName}' skipped: {Reason}", message.TaskName, gateRun.Reason);
                if (runCosts is not null)
                    await runCosts.EndRunAsync(message.TaskName, ScheduledRunOutcome.GatedSkip, report: null, ct);
                return;
            }
            logger.LogInformation("Scheduled task '{TaskName}' gate: {Reason}", message.TaskName, gateRun.Reason);
        }

        // Build full agent context using an ephemeral session ID so no history accumulates
        // across patrol runs. Pass the task description as the user content for BM25 recall.
        // workingMemoryNamespace must be passed explicitly because sessionId is "patrol/{name}",
        // not a raw session ID — without it the context builder would look in "session/patrol/{name}".
        //
        // Capabilities come from the task definition, not the live-session stash: scheduled
        // tasks fire at unpredictable times when the user may be on any client (or none), so
        // author-time intent is the only meaningful signal.
        var chatMessages = await agentContextBuilder.BuildAsync(
            sessionId, message.Description, ct,
            workingMemoryNamespace: sessionId,
            clientCapabilities: message.ClientCapabilities);

        // If a task-specific directive file exists (e.g. heartbeat-patrol.md), inject it
        // as a system message immediately after the main system prompt (index 1).
        var basePath = profileOptions.Value.BasePath;
        var directivePath = Path.Combine(basePath, $"{message.TaskName}.md");
        var nextSystemInsertIndex = 1;
        if (File.Exists(directivePath))
        {
            var directiveContent = await File.ReadAllTextAsync(directivePath, ct);
            chatMessages.Insert(nextSystemInsertIndex, new ChatMessage(ChatRole.System, directiveContent));
            nextSystemInsertIndex++;
            logger.LogInformation("Injected task directive from '{Path}'", directivePath);
        }

        // If the scheduled task carries an evolving Directive body (the agent's running checklist
        // updated via update_task_directive), inject it as the next system message so it sits
        // immediately after the static framing on every fire.
        if (!string.IsNullOrWhiteSpace(taskRecord?.Directive))
        {
            chatMessages.Insert(nextSystemInsertIndex, new ChatMessage(ChatRole.System, taskRecord.Directive));
            nextSystemInsertIndex++;
            logger.LogInformation(
                "Injected evolving task directive for '{Task}' ({Length} chars)",
                message.TaskName, taskRecord.Directive.Length);
        }

        // #848: what the gate wisp found, so the turn starts from it instead of fetching it again.
        if (gateRun?.Output is { } gateOutput && taskRecord?.Gate is { } passedGate)
            chatMessages.Insert(nextSystemInsertIndex,
                new ChatMessage(ChatRole.System, FormatGateOutput(passedGate, gateOutput, gateRun.OutputKey, taskRecord.LastFiredAt)));

        // Add the task description as the user turn (context builder doesn't add it;
        // the ephemeral session has no conversation history).
        chatMessages.Add(new ChatMessage(ChatRole.User, message.Description));

        // Per-session tools — same set the user handler builds (sessionId already is "patrol/name")
        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, sessionId, logger,
            options: workingMemoryOptions?.Value);
        var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, sessionId, skillUsageStore,
            mcpSkillSurface: mcpSkillSurface);
        var taskDirectiveTools = new TaskDirectiveTools(scheduledTaskStore, message.TaskName, logger);

        var batchId = Guid.NewGuid().ToString("N")[..12];
        var spawnedSubagent = false;
        var registryTools = toolRegistry.BuildAgentToolFunctions(
            sessionId, batchId, ToolProfiles.Scheduled,
            onInvoke: name =>
            {
                if (string.Equals(name, "spawn_subagent", StringComparison.OrdinalIgnoreCase))
                    spawnedSubagent = true;
            },
            logger: logger);

        var allTools = memoryTools.Tools
            .Concat(sessionWorkingMemoryTools.Tools)
            .Concat(sessionSkillTools.Tools)
            .Concat(taskDirectiveTools.Tools)
            .Concat(rulesTools.Tools)
            .Concat(toolGuideTools.Tools)
            .Concat(registryTools)
            .OfType<AIFunction>()
            .WithChunking(workingMemory, sessionId, modelBehavior, logger); // sessionId = "patrol/{name}"

        var chatOptions = new ChatOptions
        {
            Tools = allTools
        };

        // The scheduler already holds the work-serializer slot and passes its
        // cancellation token in via context.CancellationToken — a user session
        // starting during execution will fire that token so the LLM loop stops
        // cleanly. If preemption happens, re-throw so the scheduler can retry.

        var replySessionId = message.IsSystemTask ? WellKnownSessions.ScheduledSystem : WellKnownSessions.Scheduled;

        string finalText;
        bool succeeded;
        var diagnostics = new LoopDiagnostics();
        try
        {
            using var progressCtx = ToolProgressNotifier.SetContext(new ToolProgressContext
            {
                Source = UserProxy.ReplySource.Scheduled,
                SessionId = replySessionId,
                AgentName = agent.Name,
                ReplyTo = $"{UserProxyTopics.UserResponse}.{agent.Name}"
            });

            finalText = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, sessionId: sessionId,
                tier: turnTier,
                enableFollowUp: false, cancellationToken: ct,
                diagnostics: diagnostics,
                // #685: user-configured automation keeps acting without a fresh request.
                actionGate: ActionGateScope.ForScheduledTask,
                fetchRegistry: fetchRegistry);
            succeeded = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Let the scheduler see the cancellation so it can distinguish
            // host-shutdown from user-preemption and decide whether to retry.
            logger.LogInformation(
                "Scheduled task '{TaskName}' cancelled (host shutdown or user preemption)",
                message.TaskName);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled task '{TaskName}' failed", message.TaskName);
            finalText = $"I encountered an error while executing the scheduled task: {ex.Message}";
            succeeded = false;
        }

        if (succeeded)
            logger.LogInformation("Scheduled task '{TaskName}' completed", message.TaskName);
        else
            logger.LogInformation("Scheduled task '{TaskName}' finished with error reply", message.TaskName);

        // #848: what the turn did, for the dream's efficiency pass.
        if (runCosts is not null)
        {
            runCosts.NoteActivity(message.TaskName, new ScheduledRunActivity(
                ToolCalls: diagnostics.ToolCalls,
                DuplicateFetches: fetchRegistry.Repeats,
                SkillsUsed: await SkillsUsedSinceAsync(sessionId, runStartedAt, ct)));
            if (spawnedSubagent)
                runCosts.NoteDelegated(message.TaskName);
        }

        // #846: a run that delegated ends when its subagents' synthesis is saved
        // (SubagentResultHandler); any other run ends here.
        var costCheck = spawnedSubagent || runCosts is null
            ? null
            : await runCosts.EndRunAsync(
                message.TaskName,
                string.IsNullOrWhiteSpace(finalText) ? ScheduledRunOutcome.Empty : ScheduledRunOutcome.Replied,
                succeeded ? finalText : null,
                ct);

        // Patrol tasks may produce no output when there is nothing to report — that is correct.
        if (string.IsNullOrWhiteSpace(finalText))
        {
            logger.LogInformation("Scheduled task '{TaskName}' produced no output; suppressing reply", message.TaskName);
            return;
        }

        // If the loop spawned a subagent, the user-facing report comes from the subagent's
        // Phase 1 completion bubble plus the consolidated Phase 2 synthesis. Whatever the
        // parent loop emitted around its spawn_subagent call would just be a redundant
        // pre-results bubble.
        if (spawnedSubagent)
        {
            logger.LogInformation(
                "Scheduled task '{TaskName}' delegated to subagent — suppressing parent reply",
                message.TaskName);
            return;
        }

        var reply = new AgentReply
        {
            Source = UserProxy.ReplySource.Scheduled,
            Content = finalText,
            SessionId = replySessionId,
            AgentName = agent.Name,
            IsFinal = true,
            // Synthetic origin — scheduled tasks have no originating user session.
            Origin = new ReplyOrigin(
                Channel: "scheduled",
                PromptSummary: message.TaskName,
                StartedAt: DateTimeOffset.UtcNow,
                SessionId: replySessionId)
        };

        // #809: stored in its broadcast session so a client's reloaded history still has it.
        reply = await conversationMemory.RecordBroadcastReplyAsync(reply, logger, ct);

        var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
        await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", envelope, ct);

        // #418: keep the report after it scrolls out of every client. Labelled by task name, so the
        // store keeps only a task's latest runs. An error reply is not a report. #846: an unusually
        // expensive run says so in its saved copy.
        if (succeeded && resultArchive is not null)
            await resultArchive.ArchiveAsync(
                SavedResponseSources.Scheduled, message.TaskName, costCheck?.Annotate(finalText) ?? finalText,
                agent.Name, replySessionId, ct);
    }

    /// <summary>What a task's gate did on one run (#848).</summary>
    /// <param name="Output">The wisp's output when the gate passed; null otherwise.</param>
    internal sealed record GateRun(bool RunLlm, ScheduledRunGateResult Result, string Reason, string? Output, string? OutputKey);

    /// <summary>
    /// Runs <paramref name="gate"/>'s wisp and decides whether the LLM turn runs (#848). Anything that
    /// stops the wisp from giving an answer (no wisp subsystem, a missing resource, a failed step)
    /// fails open: the task runs as if it had no gate, so a broken gate never silences it.
    /// </summary>
    internal async Task<GateRun> RunGateAsync(
        ScheduledTask task, TaskGate gate, string sessionId, RunFetchRegistry fetchRegistry, CancellationToken ct)
    {
        if (savedWispRunner is null)
            return FailOpen("wisps are not available on this agent");

        string? body;
        try
        {
            body = await skillStore.GetResourceAsync(gate.Skill, gate.Resource);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load gate wisp {Gate} for '{Task}'", gate.Target, task.Name);
            body = null;
        }
        if (string.IsNullOrWhiteSpace(body))
            return FailOpen($"the gate wisp {gate.Target} was not found");

        body = ScheduledTaskGate.FillPlaceholders(body, task.LastFiredAt, (timeProvider ?? TimeProvider.System).GetUtcNow());

        SavedWispRun run;
        using (RunFetchRegistryContext.Set(fetchRegistry))
            run = await savedWispRunner.RunAsync(body, sessionId, ct);
        if (!run.Succeeded)
            return FailOpen($"the gate wisp {gate.Target} failed: {run.Error}");

        var decision = ScheduledTaskGate.Evaluate(gate, run.Output);
        await scheduledTaskStore.RecordGateResultAsync(task.Name, decision.LastResultHash, decision.ConsecutiveSkips);
        return new GateRun(decision.RunLlm, decision.Result, decision.Reason,
            decision.Result == ScheduledRunGateResult.Passed ? run.Output : null,
            run.OutputKey);

        GateRun FailOpen(string reason)
        {
            logger.LogWarning("Scheduled task '{Task}' runs without its gate: {Reason}", task.Name, reason);
            return new GateRun(true, ScheduledRunGateResult.FailedOpen, reason, null, null);
        }
    }

    /// <summary>The system message that hands a passed gate's output to the patrol turn.</summary>
    internal static string FormatGateOutput(TaskGate gate, string output, string? outputKey, DateTimeOffset? lastFiredAt)
    {
        var shown = output.Length > MaxGateOutputChars
            ? output[..MaxGateOutputChars] + $"\n… (truncated; {output.Length:N0} chars in all)"
            : output;
        var since = lastFiredAt is { } last ? $" since the last run ({last:yyyy-MM-dd HH:mm} UTC)" : string.Empty;
        var key = outputKey is null
            ? string.Empty
            : $" The full output is in working memory at `{outputKey}` (pass `{WorkerInputKey(outputKey)}` as a worker's input_keys to have a worker go through it).";
        return $"""
            ## Pre-fetched by this task's gate wisp ({gate.Target})
            This task's saved wisp has already run the fixed fetch steps{since} and found the results below.
            Start from them: do not repeat those fetches. Only look further for what the results point to.{key}

            {shown}
            """;
    }

    /// <summary><c>wisp/{id}/{step}/output</c> as a worker's <c>wisp:{id}/{step}</c> reference.</summary>
    internal static string WorkerInputKey(string outputKey)
    {
        const string prefix = "wisp/", suffix = "/output";
        return outputKey.StartsWith(prefix, StringComparison.Ordinal) && outputKey.EndsWith(suffix, StringComparison.Ordinal)
            ? "wisp:" + outputKey[prefix.Length..^suffix.Length]
            : outputKey;
    }

    /// <summary>The skills the patrol session loaded since <paramref name="since"/>, in order.</summary>
    private async Task<IReadOnlyList<string>?> SkillsUsedSinceAsync(string sessionId, DateTimeOffset since, CancellationToken ct)
    {
        if (skillUsageStore is null)
            return null;
        try
        {
            var events = await skillUsageStore.GetBySessionAsync(sessionId, ct);
            return
            [
                .. events.Where(e => e.Timestamp >= since)
                    .Select(e => e.SkillName)
                    .Distinct(StringComparer.OrdinalIgnoreCase),
            ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read skill usage for {Session}", sessionId);
            return null;
        }
    }
}
