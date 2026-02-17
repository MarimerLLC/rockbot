using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.SampleAgent;

/// <summary>
/// Handles incoming <see cref="UserMessage"/> by calling the LLM and publishing
/// an <see cref="AgentReply"/> back to the user. Maintains conversation history
/// and executes tool calls in an explicit loop for full control over the
/// call → tool → result → response cycle.
/// </summary>
internal sealed class UserMessageHandler(
    IChatClient chatClient,
    IMessagePublisher publisher,
    AgentIdentity agent,
    AgentProfile profile,
    ISystemPromptBuilder promptBuilder,
    IConversationMemory conversationMemory,
    MemoryTools memoryTools,
    ILogger<UserMessageHandler> logger) : IMessageHandler<UserMessage>
{
    /// <summary>
    /// Maximum conversation turns to include in the LLM prompt.
    /// Keeps context size bounded regardless of how many turns are stored.
    /// </summary>
    private const int MaxLlmContextTurns = 20;

    /// <summary>
    /// Maximum number of tool-calling round-trips before forcing a final text response.
    /// </summary>
    private const int MaxToolIterations = 5;

    public async Task HandleAsync(UserMessage message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo ?? UserProxyTopics.UserResponse;
        var correlationId = context.Envelope.CorrelationId;

        logger.LogInformation("Received message from {UserId} in session {SessionId}: {Content}",
            message.UserId, message.SessionId, message.Content);

        try
        {
            // Record the incoming user turn
            await conversationMemory.AddTurnAsync(
                message.SessionId,
                new ConversationTurn("user", message.Content, DateTimeOffset.UtcNow),
                context.CancellationToken);

            // Build chat messages: system prompt + recent conversation history
            var systemPrompt = promptBuilder.Build(profile, agent);
            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt)
            };

            // Replay only recent history to keep LLM context bounded
            var history = await conversationMemory.GetTurnsAsync(
                message.SessionId, context.CancellationToken);

            var startIndex = Math.Max(0, history.Count - MaxLlmContextTurns);
            for (var i = startIndex; i < history.Count; i++)
            {
                var turn = history[i];
                var role = turn.Role == "user" ? ChatRole.User : ChatRole.Assistant;
                chatMessages.Add(new ChatMessage(role, turn.Content));
            }

            var chatOptions = new ChatOptions
            {
                Tools = memoryTools.Tools
            };

            // Tool-calling loop: call LLM, execute any tool calls, feed results back, repeat
            var content = await CallWithToolLoopAsync(chatMessages, chatOptions, context.CancellationToken);

            // Record the assistant turn (includes tool results incorporated by the LLM)
            await conversationMemory.AddTurnAsync(
                message.SessionId,
                new ConversationTurn("assistant", content, DateTimeOffset.UtcNow),
                context.CancellationToken);

            var reply = new AgentReply
            {
                Content = content,
                SessionId = message.SessionId,
                AgentName = agent.Name,
                IsFinal = true
            };

            var envelope = reply.ToEnvelope<AgentReply>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);

            logger.LogInformation("Published reply to {ReplyTo} for correlation {CorrelationId}",
                replyTo, correlationId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process user message {CorrelationId}", correlationId);

            var errorReply = new AgentReply
            {
                Content = $"Sorry, I encountered an error: {ex.Message}",
                SessionId = message.SessionId,
                AgentName = agent.Name,
                IsFinal = true
            };

            var envelope = errorReply.ToEnvelope<AgentReply>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);
        }
    }

    /// <summary>
    /// Calls the LLM and handles any tool calls in an explicit loop.
    /// After each LLM response, checks for <see cref="FunctionCallContent"/>; if present,
    /// executes the tools, appends results, and calls the LLM again. Repeats until the
    /// LLM returns a plain text response (no tool calls) or the iteration limit is reached.
    /// </summary>
    private async Task<string> CallWithToolLoopAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        // Log the tools being offered to the LLM
        var toolNames = chatOptions.Tools?
            .OfType<AIFunction>()
            .Select(t => t.Name)
            .ToList() ?? [];
        logger.LogInformation("Calling LLM with {ToolCount} tools: [{Tools}]",
            toolNames.Count, string.Join(", ", toolNames));

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var sw = Stopwatch.StartNew();
            var response = await chatClient.GetResponseAsync(
                chatMessages, chatOptions, cancellationToken);
            sw.Stop();

            // Log response structure for diagnostics
            logger.LogInformation(
                "LLM responded in {ElapsedMs}ms — {MsgCount} message(s), iteration {Iteration}",
                sw.ElapsedMilliseconds, response.Messages.Count, iteration + 1);

            for (var i = 0; i < response.Messages.Count; i++)
            {
                var msg = response.Messages[i];
                var contentParts = string.Join(", ", msg.Contents.Select(c => c.GetType().Name));
                logger.LogInformation(
                    "  Message[{Index}] role={Role} text={TextLen} chars, contents=[{ContentParts}]",
                    i, msg.Role, msg.Text?.Length ?? 0, contentParts);
            }

            // Collect any tool calls from the response
            var functionCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            logger.LogInformation("  FunctionCallContent count: {Count}", functionCalls.Count);

            if (functionCalls.Count == 0)
            {
                // No tool calls — this is the final text response
                var text = ExtractAssistantText(response);
                logger.LogInformation("Final response text ({Length} chars): {Preview}",
                    text.Length, text.Length > 200 ? text[..200] + "..." : text);
                return text;
            }

            logger.LogInformation(
                "LLM requested {Count} tool call(s) on iteration {Iteration}",
                functionCalls.Count, iteration + 1);

            // Add the assistant message(s) containing the tool calls to the conversation
            chatMessages.AddRange(response.Messages);

            // Execute each tool call and feed results back
            foreach (var fc in functionCalls)
            {
                var argsSummary = fc.Arguments is not null
                    ? string.Join(", ", fc.Arguments.Select(a => $"{a.Key}={a.Value}"))
                    : "(none)";
                logger.LogInformation("Executing tool {Name}(callId={CallId}, args={Args})",
                    fc.Name, fc.CallId, argsSummary);

                var tool = chatOptions.Tools?
                    .OfType<AIFunction>()
                    .FirstOrDefault(t => t.Name.Equals(fc.Name, StringComparison.OrdinalIgnoreCase));

                if (tool is null)
                {
                    logger.LogWarning("LLM requested unknown tool: {Name}", fc.Name);
                    chatMessages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(fc.CallId, $"Error: unknown tool '{fc.Name}'")]));
                    continue;
                }

                var args = fc.Arguments is not null
                    ? new AIFunctionArguments(fc.Arguments!)
                    : new AIFunctionArguments();
                var toolSw = Stopwatch.StartNew();
                var result = await tool.InvokeAsync(args, cancellationToken);
                toolSw.Stop();

                logger.LogInformation("Tool {Name} returned in {ElapsedMs}ms: {Result}",
                    fc.Name, toolSw.ElapsedMilliseconds, result);

                chatMessages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(fc.CallId, result)]));
            }

            // On the last iteration, remove tools so the LLM must produce a text response
            if (iteration == MaxToolIterations - 2)
            {
                chatOptions = new ChatOptions();
            }
        }

        // Exhausted iterations — one last call without tools to force a text response
        logger.LogWarning("Tool loop reached {Max} iterations; forcing final response", MaxToolIterations);
        var finalResponse = await chatClient.GetResponseAsync(
            chatMessages, new ChatOptions(), cancellationToken);
        return ExtractAssistantText(finalResponse);
    }

    /// <summary>
    /// Extracts text from the LLM response, walking backwards to find
    /// the last assistant message with non-empty text content.
    /// </summary>
    private string ExtractAssistantText(ChatResponse response)
    {
        for (var i = response.Messages.Count - 1; i >= 0; i--)
        {
            var msg = response.Messages[i];
            if (msg.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(msg.Text))
                return msg.Text;
        }

        // Fallback: concatenated text from all messages
        if (!string.IsNullOrWhiteSpace(response.Text))
            return response.Text;

        logger.LogWarning("LLM response contained no usable text across {Count} messages",
            response.Messages.Count);
        return string.Empty;
    }
}
