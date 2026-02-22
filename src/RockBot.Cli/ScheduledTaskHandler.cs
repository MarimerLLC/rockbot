using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.Cli;

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
    ModelBehavior modelBehavior,
    AgentLoopRunner agentLoopRunner,
    ILogger<ScheduledTaskHandler> logger) : IMessageHandler<ScheduledTaskMessage>
{
    public async Task HandleAsync(ScheduledTaskMessage message, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;
        logger.LogInformation("Executing scheduled task '{TaskName}'", message.TaskName);

        var resultInstruction = modelBehavior.ScheduledTaskResultMode switch
        {
            ScheduledTaskResultMode.VerbatimOutput =>
                "When presenting results, include the complete verbatim output from every " +
                "tool you called. Do not paraphrase, summarise, or describe what the output " +
                "showed — paste it in full so the user can see the actual content.",
            ScheduledTaskResultMode.SummarizeWithOutput =>
                "When presenting results, first write a brief natural-language summary, " +
                "then include the complete verbatim output from every tool you called.",
            _ =>
                "When you are done, present the result directly and naturally — as if you " +
                "are proactively messaging the user with completed work."
        };

        var systemPrompt =
            "You are an autonomous agent. A background scheduled task has just fired and you " +
            $"are executing it now. Use your available tools as needed to complete the work. " +
            $"{resultInstruction} " +
            "Do not say 'I was asked to' or reference the scheduling system; just deliver the result clearly.";

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
        };

        if (!string.IsNullOrEmpty(modelBehavior.AdditionalSystemPrompt))
            chatMessages.Add(new ChatMessage(ChatRole.System, modelBehavior.AdditionalSystemPrompt));

        chatMessages.Add(new ChatMessage(ChatRole.User, message.Description));

        var registryTools = toolRegistry.GetTools()
            .Select(r => (AIFunction)new RegistryToolFunction(r, toolRegistry.GetExecutor(r.Name)!, sessionId: null))
            .ToArray();

        var chatOptions = new ChatOptions
        {
            Tools = [..rulesTools.Tools, ..registryTools]
        };

        string finalText;
        try
        {
            finalText = await agentLoopRunner.RunAsync(chatMessages, chatOptions, sessionId: null, cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled task '{TaskName}' failed", message.TaskName);
            finalText = $"I encountered an error while executing the scheduled task: {ex.Message}";
        }

        logger.LogInformation("Scheduled task '{TaskName}' completed", message.TaskName);

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
