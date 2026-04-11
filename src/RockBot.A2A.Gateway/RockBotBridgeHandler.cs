using System.Security.Claims;
using A2A;
using Microsoft.Extensions.Options;

using A2ATaskStatus = A2A.TaskStatus;
using RockBot.Messaging;

using RbAgentTaskRequest = RockBot.A2A.AgentTaskRequest;
using RbAgentTaskResult = RockBot.A2A.AgentTaskResult;
using RbAgentTaskCancelRequest = RockBot.A2A.AgentTaskCancelRequest;
using RbAgentMessage = RockBot.A2A.AgentMessage;
using RbAgentMessagePart = RockBot.A2A.AgentMessagePart;
using RbAgentTaskStatusUpdate = RockBot.A2A.AgentTaskStatusUpdate;

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
    IOptions<GatewayOptions> gatewayOptions,
    ITaskStore taskStore,
    PushNotificationSender pushSender,
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
        var timeout = TimeSpan.FromSeconds(gatewayOptions.Value.TaskTimeoutSeconds);

        logger.LogInformation(
            "Bridging A2A task {TaskId} skill={Skill} caller={CallerId} streaming={Streaming} to RockBot via RabbitMQ",
            taskId, skill, callerId, context.StreamingResponse);

        // Subscribe for the response BEFORE publishing
        var resultTcs = new TaskCompletionSource<RbAgentTaskResult>();
        var subName = $"a2a-gw-{Guid.NewGuid():N}";
        await using var replySub = await subscriber.SubscribeAsync(
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

        // Subscribe to status updates for intermediate streaming events
        IAsyncDisposable? statusSub = null;
        if (context.StreamingResponse)
        {
            var statusSubName = $"a2a-gw-status-{Guid.NewGuid():N}";
            statusSub = await subscriber.SubscribeAsync(
                "agent.task.status",
                statusSubName,
                async (envelope, _) =>
                {
                    try
                    {
                        var update = envelope.GetPayload<RbAgentTaskStatusUpdate>();
                        if (update is not null && envelope.CorrelationId == taskId)
                        {
                            var statusText = update.Message?.Parts
                                .Where(p => p.Kind == "text")
                                .Select(p => p.Text)
                                .FirstOrDefault();

                            var statusEvent = new TaskStatusUpdateEvent
                            {
                                TaskId = taskId,
                                ContextId = context.ContextId,
                                Status = new A2ATaskStatus
                                {
                                    State = MapTaskState(update.State),
                                    Message = statusText is not null
                                        ? new Message { Role = Role.Agent, Parts = [new Part { Text = statusText }] }
                                        : null,
                                    Timestamp = DateTimeOffset.UtcNow
                                }
                            };
                            await eventQueue.EnqueueStatusUpdateAsync(statusEvent, cancellationToken);
                            // Fire-and-forget push notification
                            var __ = pushSender.TrySendStatusUpdateAsync(taskId, statusEvent, cancellationToken);
                        }
                    }
                    catch { /* ignore deserialization errors */ }
                    return MessageResult.Ack;
                },
                cancellationToken);
        }

        try
        {
            // Brief delay for subscriptions to bind
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
            var result = await resultTcs.Task.WaitAsync(timeout, cancellationToken);

            logger.LogInformation("Got response for task {TaskId}: state={State}", taskId, result.State);

            // Map RockBot result back to A2A v1 Message
            var responseText = result.Message?.Parts
                .Where(p => p.Kind == "text")
                .Select(p => p.Text)
                .FirstOrDefault() ?? "(no response)";

            var responseMessage = new Message
            {
                Role = Role.Agent,
                Parts = [new Part { Text = responseText }]
            };

            // Persist the completed task so ListTasks can return it.
            // The SDK's A2AServer manages task state in memory for synchronous
            // SendMessage flows without calling SaveTaskAsync, so we save directly.
            var completedTask = new AgentTask
            {
                Id = taskId,
                ContextId = context.ContextId,
                Status = new A2ATaskStatus
                {
                    State = MapTaskState(result.State),
                    Message = responseMessage,
                    Timestamp = DateTimeOffset.UtcNow
                },
                History = [
                    new Message
                    {
                        Role = Role.User,
                        Parts = [new Part { Text = messageText }]
                    },
                    responseMessage
                ]
            };
            await taskStore.SaveTaskAsync(taskId, completedTask, cancellationToken);
            _ = pushSender.TrySendTaskCompletedAsync(taskId, completedTask, cancellationToken);

            await eventQueue.EnqueueMessageAsync(responseMessage, cancellationToken);
        }
        finally
        {
            if (statusSub is not null)
                await statusSub.DisposeAsync();
        }
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

    private static TaskState MapTaskState(AgentTaskState state) => state switch
    {
        AgentTaskState.Submitted => TaskState.Submitted,
        AgentTaskState.Working => TaskState.Working,
        AgentTaskState.InputRequired => TaskState.InputRequired,
        AgentTaskState.Completed => TaskState.Completed,
        AgentTaskState.Failed => TaskState.Failed,
        AgentTaskState.Canceled => TaskState.Canceled,
        _ => TaskState.Working
    };
}
