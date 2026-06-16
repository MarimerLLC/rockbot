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
    ISkillUsageStore? skillUsageStore = null) : IMessageHandler<ScheduledTaskMessage>
{
    public async Task HandleAsync(ScheduledTaskMessage message, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;
        var sessionId = $"patrol/{message.TaskName}";
        logger.LogInformation("Executing scheduled task '{TaskName}'", message.TaskName);

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
        var taskRecord = await scheduledTaskStore.GetAsync(message.TaskName);
        if (!string.IsNullOrWhiteSpace(taskRecord?.Directive))
        {
            chatMessages.Insert(nextSystemInsertIndex, new ChatMessage(ChatRole.System, taskRecord.Directive));
            logger.LogInformation(
                "Injected evolving task directive for '{Task}' ({Length} chars)",
                message.TaskName, taskRecord.Directive.Length);
        }

        // Add the task description as the user turn (context builder doesn't add it;
        // the ephemeral session has no conversation history).
        chatMessages.Add(new ChatMessage(ChatRole.User, message.Description));

        // Per-session tools — same set the user handler builds (sessionId already is "patrol/name")
        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, sessionId, logger);
        var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, sessionId, skillUsageStore);
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

        var replySessionId = message.IsSystemTask ? "scheduled-system" : "scheduled";

        string finalText;
        bool succeeded;
        try
        {
            using var progressCtx = ToolProgressNotifier.SetContext(new ToolProgressContext
            {
                SessionId = replySessionId,
                AgentName = agent.Name,
                ReplyTo = $"{UserProxyTopics.UserResponse}.{agent.Name}"
            });

            finalText = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, sessionId: sessionId,
                enableFollowUp: false, cancellationToken: ct);
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

        var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
        await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", envelope, ct);
    }
}
