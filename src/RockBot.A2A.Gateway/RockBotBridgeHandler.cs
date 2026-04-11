using System.Security.Claims;
using A2A;
using RockBot.Messaging;

using RbAgentTaskRequest = RockBot.A2A.AgentTaskRequest;
using RbAgentTaskResult = RockBot.A2A.AgentTaskResult;
using RbAgentTaskCancelRequest = RockBot.A2A.AgentTaskCancelRequest;
using RbAgentMessage = RockBot.A2A.AgentMessage;
using RbAgentMessagePart = RockBot.A2A.AgentMessagePart;

namespace RockBot.A2A.Gateway;

/// <summary>
/// Bridges A2A v1 server requests to RockBot's RabbitMQ message handler.
/// Extracts the authenticated caller's identity from <see cref="IHttpContextAccessor"/>
/// and propagates it as the <c>Source</c> on the RabbitMQ envelope so the trust model
/// sees the real caller, not the gateway.
/// </summary>
internal sealed class RockBotBridgeHandler(
    IMessagePublisher publisher,
    IMessageSubscriber subscriber,
    IHttpContextAccessor httpContextAccessor,
    ILogger<RockBotBridgeHandler> logger) : IAgentHandler
{
    private string GetCallerId() =>
        httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? "anonymous";

    private static string ReplyTopicFor(string callerId) =>
        $"agent.response.gateway.{callerId}";

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var callerId = GetCallerId();
        var replyTopic = ReplyTopicFor(callerId);
        var taskId = context.TaskId ?? Guid.NewGuid().ToString("N");
        var skill = "general";
        if (context.Metadata?.TryGetValue("skill", out var skillEl) == true)
            skill = skillEl.GetString() ?? "general";

        var messageText = context.UserText ?? "(empty)";

        logger.LogInformation(
            "Bridging A2A task {TaskId} skill={Skill} caller={CallerId} to RockBot via RabbitMQ",
            taskId, skill, callerId);

        // Subscribe for the response BEFORE publishing
        var resultTcs = new TaskCompletionSource<RbAgentTaskResult>();
        var subName = $"a2a-gw-{Guid.NewGuid():N}";
        await using var sub = await subscriber.SubscribeAsync(
            replyTopic,
            subName,
            (envelope, _) =>
            {
                try
                {
                    var result = envelope.GetPayload<RbAgentTaskResult>();
                    if (result?.TaskId == taskId)
                        resultTcs.TrySetResult(result);
                }
                catch { /* ignore deserialization errors from unrelated messages */ }
                return Task.FromResult(MessageResult.Ack);
            },
            cancellationToken);

        // Brief delay for subscription to bind
        await Task.Delay(300, cancellationToken);

        // Publish task to RockBot
        var request = new RbAgentTaskRequest
        {
            TaskId = taskId,
            Skill = skill,
            Message = new RbAgentMessage
            {
                Role = "user",
                Parts = [new RbAgentMessagePart { Kind = "text", Text = messageText }]
            }
        };

        var envelope = request.ToEnvelope<RbAgentTaskRequest>(
            source: callerId,
            correlationId: taskId,
            replyTo: replyTopic);

        await publisher.PublishAsync("agent.task.RockBot", envelope, cancellationToken);

        // Wait for RockBot's response
        var result = await resultTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

        logger.LogInformation("Got response for task {TaskId}: state={State}", taskId, result.State);

        // Map RockBot result back to A2A v1 Message
        var responseText = result.Message?.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault() ?? "(no response)";

        await eventQueue.EnqueueMessageAsync(new Message
        {
            Role = Role.Agent,
            Parts = [new Part { Text = responseText }]
        }, cancellationToken);
    }

    public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var callerId = GetCallerId();
        var taskId = context.TaskId;

        logger.LogInformation("Cancel requested for task {TaskId} by {CallerId}", taskId, callerId);

        if (taskId is null)
            return;

        var cancelRequest = new RbAgentTaskCancelRequest { TaskId = taskId };
        var envelope = cancelRequest.ToEnvelope<RbAgentTaskCancelRequest>(
            source: callerId,
            correlationId: taskId);

        await publisher.PublishAsync("agent.task.cancel.RockBot", envelope, cancellationToken);
    }
}
