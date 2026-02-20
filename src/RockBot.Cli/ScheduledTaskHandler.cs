using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.Cli;

/// <summary>
/// Handles <see cref="ScheduledTaskMessage"/> by invoking the LLM with the full agent
/// tool set and publishing the result as an <see cref="AgentReply"/> so the user sees
/// it in the UI. The LLM is told it is proactively delivering the result of a background
/// task, so it frames its response naturally rather than as a reply to a user question.
/// </summary>
internal sealed class ScheduledTaskHandler(
    ILlmClient llmClient,
    IMessagePublisher publisher,
    AgentIdentity agent,
    IToolRegistry toolRegistry,
    RulesTools rulesTools,
    ILogger<ScheduledTaskHandler> logger) : IMessageHandler<ScheduledTaskMessage>
{
    private const int MaxToolIterations = 8;

    public async Task HandleAsync(ScheduledTaskMessage message, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;
        logger.LogInformation("Executing scheduled task '{TaskName}'", message.TaskName);

        var systemPrompt =
            "You are an autonomous agent. A background scheduled task has just fired and you " +
            "are executing it now. Use your available tools as needed to complete the work. " +
            "When you are done, present the result directly and naturally — as if you are " +
            "proactively messaging the user with completed work. Do not say 'I was asked to' " +
            "or reference the scheduling system; just deliver the result clearly.";

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, message.Description)
        };

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
            finalText = await RunToolLoopAsync(chatMessages, chatOptions, ct);
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

    private async Task<string> RunToolLoopAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        CancellationToken ct)
    {
        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var response = await llmClient.GetResponseAsync(chatMessages, chatOptions, ct);

            var functionCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            if (functionCalls.Count == 0)
            {
                // No tool calls — this is the final text response
                for (var i = response.Messages.Count - 1; i >= 0; i--)
                {
                    var msg = response.Messages[i];
                    if (msg.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(msg.Text))
                        return msg.Text.Trim();
                }
                return response.Text?.Trim() ?? string.Empty;
            }

            logger.LogInformation(
                "Scheduled task tool loop iteration {N}: {Count} tool call(s)",
                iteration + 1, functionCalls.Count);

            chatMessages.AddRange(response.Messages);

            foreach (var fc in functionCalls)
            {
                var tool = chatOptions.Tools?
                    .OfType<AIFunction>()
                    .FirstOrDefault(t => t.Name.Equals(fc.Name, StringComparison.OrdinalIgnoreCase));

                object? result;
                if (tool is null)
                {
                    result = $"Error: unknown tool '{fc.Name}'";
                }
                else
                {
                    var args = fc.Arguments is not null
                        ? new AIFunctionArguments(fc.Arguments!)
                        : new AIFunctionArguments();
                    try
                    {
                        result = await tool.InvokeAsync(args, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Tool '{Name}' threw in scheduled task loop", fc.Name);
                        result = $"Error: {ex.Message}";
                    }
                }

                chatMessages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(fc.CallId, result)]));
            }

            // On the last iteration strip tools so the LLM must produce a text response
            if (iteration == MaxToolIterations - 2)
                chatOptions = new ChatOptions();
        }

        // Exhausted iterations — force a final text response without tools
        var finalResponse = await llmClient.GetResponseAsync(chatMessages, new ChatOptions(), ct);
        return finalResponse.Text?.Trim() ?? string.Empty;
    }
}
