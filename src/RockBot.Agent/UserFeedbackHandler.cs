using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="UserFeedback"/> messages published when a user clicks
/// thumbs up or thumbs down in the chat UI.
///
/// <list type="bullet">
///   <item><b>Positive:</b> records the signal in the feedback store and appends a
///   reinforcement note to conversation history so the agent continues the pattern.</item>
///   <item><b>Negative:</b> records the signal, appends a re-evaluation nudge to
///   conversation history, calls the LLM with the full agent context to produce a
///   fresh response, and publishes an unsolicited <see cref="AgentReply"/>.</item>
/// </list>
/// </summary>
internal sealed class UserFeedbackHandler(
    IConversationMemory conversationMemory,
    IMessagePublisher publisher,
    AgentIdentity agent,
    IFeedbackStore feedbackStore,
    AgentLoopRunner agentLoopRunner,
    AgentContextBuilder agentContextBuilder,
    IToolRegistry toolRegistry,
    RulesTools rulesTools,
    IAgentWorkSerializer workSerializer,
    ILogger<UserFeedbackHandler> logger) : IMessageHandler<UserFeedback>
{
    public async Task HandleAsync(UserFeedback message, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;

        logger.LogInformation(
            "Received {FeedbackType} feedback for message {MessageId} in session {SessionId}",
            message.IsPositive ? "positive" : "negative",
            message.MessageId,
            message.SessionId);

        // Record the explicit signal in the feedback store for the dream service.
        _ = feedbackStore.AppendAsync(new FeedbackEntry(
            Id: Guid.NewGuid().ToString("N")[..12],
            SessionId: message.SessionId,
            SignalType: message.IsPositive
                ? FeedbackSignalType.UserThumbsUp
                : FeedbackSignalType.UserThumbsDown,
            Summary: message.IsPositive
                ? "User marked reply as helpful"
                : "User marked reply as unhelpful",
            Detail: $"MessageId={message.MessageId}, Agent={message.AgentName ?? "(unknown)"}",
            Timestamp: DateTimeOffset.UtcNow), ct);

        if (message.IsPositive)
        {
            await HandlePositiveFeedbackAsync(message, ct);
        }
        else
        {
            await HandleNegativeFeedbackAsync(message, ct);
        }
    }

    private async Task HandlePositiveFeedbackAsync(UserFeedback message, CancellationToken ct)
    {
        // Append a reinforcement note so future turns benefit from the signal.
        await conversationMemory.AddTurnAsync(
            message.SessionId,
            new ConversationTurn(
                "system",
                "[The user indicated the previous response was helpful — reinforce this approach.]",
                DateTimeOffset.UtcNow),
            ct);

        logger.LogInformation(
            "Recorded positive feedback reinforcement for session {SessionId}",
            message.SessionId);
    }

    private async Task HandleNegativeFeedbackAsync(UserFeedback message, CancellationToken ct)
    {
        // Retrieve conversation history to find the last user message for context building.
        var turns = await conversationMemory.GetTurnsAsync(message.SessionId, ct);
        var lastUserTurn = turns.LastOrDefault(t => t.Role == "user");

        if (lastUserTurn is null)
        {
            logger.LogWarning(
                "No user turn found in session {SessionId}; cannot re-evaluate", message.SessionId);
            return;
        }

        // Try to acquire the execution slot as a scheduled task — yields to user messages.
        var slot = await workSerializer.TryAcquireForScheduledAsync(ct);
        if (slot is null)
        {
            logger.LogInformation(
                "Skipping re-evaluation for session {SessionId} — user session is active",
                message.SessionId);
            return;
        }

        string freshResponse;
        try
        {
            await using (slot)
            {
                // Build the full agent context using the last user message for BM25 recall.
                var chatMessages = await agentContextBuilder.BuildAsync(
                    message.SessionId, lastUserTurn.Content, slot.Token);

                // Add a nudge so the LLM knows to try a different approach.
                chatMessages.Add(new ChatMessage(ChatRole.System,
                    "The user indicated your most recent response was not helpful (thumbs down). " +
                    "Re-read the conversation and provide a better response. " +
                    "Try a different approach, add more detail, or correct any mistakes."));

                // Give the re-evaluation loop access to registry tools + rules tools
                // so it can take actions (web search, MCP tools, etc.) if needed.
                var registryTools = toolRegistry.GetTools()
                    .Select(r => (AIFunction)new RegistryToolFunction(
                        r, toolRegistry.GetExecutor(r.Name)!, message.SessionId))
                    .ToArray();

                var chatOptions = new ChatOptions
                {
                    Tools = [.. rulesTools.Tools, .. registryTools]
                };

                freshResponse = await agentLoopRunner.RunAsync(
                    chatMessages, chatOptions, message.SessionId,
                    cancellationToken: slot.Token);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(
                "Re-evaluation for session {SessionId} was cancelled", message.SessionId);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Re-evaluation failed for session {SessionId}", message.SessionId);
            freshResponse = $"Sorry, I tried to re-evaluate but encountered an error: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(freshResponse))
        {
            logger.LogInformation(
                "Re-evaluation produced no output for session {SessionId}; suppressing reply",
                message.SessionId);
            return;
        }

        // Record the re-evaluation in conversation history.
        await conversationMemory.AddTurnAsync(
            message.SessionId,
            new ConversationTurn("assistant", freshResponse, DateTimeOffset.UtcNow),
            ct);

        // Publish the fresh reply as an unsolicited message — the proxy forwards
        // unsolicited replies directly to the frontend.
        var reply = new AgentReply
        {
            Content = freshResponse,
            SessionId = message.SessionId,
            AgentName = agent.Name,
            IsFinal = true
        };

        var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
        await publisher.PublishAsync(UserProxyTopics.UserResponse, envelope, ct);

        logger.LogInformation(
            "Published re-evaluated reply for session {SessionId} ({Length} chars)",
            message.SessionId, freshResponse.Length);
    }
}
