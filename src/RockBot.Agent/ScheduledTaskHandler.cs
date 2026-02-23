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
    IAgentWorkSerializer workSerializer,
    AgentLoopRunner agentLoopRunner,
    AgentContextBuilder agentContextBuilder,
    IOptions<AgentProfileOptions> profileOptions,
    ILogger<ScheduledTaskHandler> logger,
    ISkillUsageStore? skillUsageStore = null) : IMessageHandler<ScheduledTaskMessage>
{
    public async Task HandleAsync(ScheduledTaskMessage message, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;
        var sessionId = $"patrol-{message.TaskName}";
        logger.LogInformation("Executing scheduled task '{TaskName}'", message.TaskName);

        // Build full agent context using an ephemeral session ID so no history accumulates
        // across patrol runs. Pass the task description as the user content for BM25 recall.
        var chatMessages = await agentContextBuilder.BuildAsync(
            sessionId, message.Description, ct);

        // If a task-specific directive file exists (e.g. heartbeat-patrol.md), inject it
        // as a system message immediately after the main system prompt (index 1).
        var basePath = profileOptions.Value.BasePath;
        var directivePath = Path.Combine(basePath, $"{message.TaskName}.md");
        if (File.Exists(directivePath))
        {
            var directiveContent = await File.ReadAllTextAsync(directivePath, ct);
            chatMessages.Insert(1, new ChatMessage(ChatRole.System, directiveContent));
            logger.LogInformation("Injected task directive from '{Path}'", directivePath);
        }

        // Add the task description as the user turn (context builder doesn't add it;
        // the ephemeral session has no conversation history).
        chatMessages.Add(new ChatMessage(ChatRole.User, message.Description));

        // Per-session tools — same set the user handler builds
        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, sessionId, logger);
        var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, sessionId, skillUsageStore);

        var registryTools = toolRegistry.GetTools()
            .Select(r => (AIFunction)new RegistryToolFunction(r, toolRegistry.GetExecutor(r.Name)!, sessionId))
            .ToArray();

        var allTools = memoryTools.Tools
            .Concat(sessionWorkingMemoryTools.Tools)
            .Concat(sessionSkillTools.Tools)
            .Concat(rulesTools.Tools)
            .Concat(toolGuideTools.Tools)
            .Concat(registryTools)
            .OfType<AIFunction>()
            .WithChunking(workingMemory, sessionId, modelBehavior, logger);

        var chatOptions = new ChatOptions
        {
            Tools = allTools
        };

        // Try to acquire the single execution slot. If a user loop is running,
        // skip this tick — the next scheduled cron tick will try again.
        var slot = await workSerializer.TryAcquireForScheduledAsync(ct);
        if (slot is null)
        {
            logger.LogInformation(
                "Skipping scheduled task '{TaskName}' — user session is active", message.TaskName);
            return;
        }

        string finalText;
        try
        {
            // Use slot.Token so a new user message can preempt this task cleanly.
            await using (slot)
            {
                finalText = await agentLoopRunner.RunAsync(
                    chatMessages, chatOptions, sessionId: sessionId, cancellationToken: slot.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            // Preempted by a user session — exit silently, no error to report.
            logger.LogInformation(
                "Scheduled task '{TaskName}' was preempted by a user session", message.TaskName);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled task '{TaskName}' failed", message.TaskName);
            finalText = $"I encountered an error while executing the scheduled task: {ex.Message}";
        }

        logger.LogInformation("Scheduled task '{TaskName}' completed", message.TaskName);

        // Patrol tasks may produce no output when there is nothing to report — that is correct.
        if (string.IsNullOrWhiteSpace(finalText))
        {
            logger.LogInformation("Scheduled task '{TaskName}' produced no output; suppressing reply", message.TaskName);
            return;
        }

        var reply = new AgentReply
        {
            Content = finalText,
            SessionId = "scheduled",
            AgentName = agent.Name,
            IsFinal = true
        };

        var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
        await publisher.PublishAsync(UserProxyTopics.UserResponse, envelope, ct);
    }
}
