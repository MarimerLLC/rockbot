using Microsoft.Extensions.DependencyInjection;
using RockBot.Messaging;

namespace RockBot.A2A.IntegrationTests.Scenarios;

/// <summary>
/// Tests RockBot's inbound A2A task handling via RabbitMQ.
/// </summary>
internal static class InboundTaskScenarios
{
    private const string HarnessIdentity = "TestHarness";
    private const string ReplyTopic = $"agent.response.{HarnessIdentity}";
    private const string StatusTopic = "agent.task.status";

    /// <summary>
    /// Scenario 4: Send a notify-user task to RockBot and verify it completes.
    /// </summary>
    public static async Task SendNotifyUserAsync(IServiceProvider services, CancellationToken ct)
    {
        var publisher = services.GetRequiredService<IMessagePublisher>();
        var subscriber = services.GetRequiredService<IMessageSubscriber>();

        var taskId = Guid.NewGuid().ToString("N");

        // Subscribe to the reply topic BEFORE publishing
        var resultTcs = new TaskCompletionSource<AgentTaskResult>();
        var resultSubName = $"a2a-test-result-{Guid.NewGuid():N}";
        await using var resultSub = await subscriber.SubscribeAsync(
            ReplyTopic,
            resultSubName,
            (envelope, _) =>
            {
                try
                {
                    var result = envelope.GetPayload<AgentTaskResult>();
                    if (result?.TaskId == taskId)
                        resultTcs.TrySetResult(result);
                }
                catch { /* ignore deserialization failures */ }
                return Task.FromResult(MessageResult.Ack);
            },
            ct);

        // Subscribe to status topic for Working update
        var statusTcs = new TaskCompletionSource<AgentTaskStatusUpdate>();
        var statusSubName = $"a2a-test-status-{Guid.NewGuid():N}";
        await using var statusSub = await subscriber.SubscribeAsync(
            StatusTopic,
            statusSubName,
            (envelope, _) =>
            {
                try
                {
                    var status = envelope.GetPayload<AgentTaskStatusUpdate>();
                    if (status?.TaskId == taskId && status.State == AgentTaskState.Working)
                        statusTcs.TrySetResult(status);
                }
                catch { /* ignore */ }
                return Task.FromResult(MessageResult.Ack);
            },
            ct);

        // Brief delay to let subscriptions bind to the exchange
        await Task.Delay(500, ct);

        // Publish the task request
        var request = new AgentTaskRequest
        {
            TaskId = taskId,
            Skill = "notify-user",
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = "Integration test notification from TestHarness" }]
            }
        };

        var envelope = request.ToEnvelope<AgentTaskRequest>(
            source: HarnessIdentity,
            correlationId: taskId,
            replyTo: ReplyTopic);

        await publisher.PublishAsync("agent.task.RockBot", envelope, ct);

        // Wait for Working status
        var status = await statusTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert(status.State == AgentTaskState.Working,
            $"Expected Working status, got {status.State}");

        // Wait for the final result
        var result = await resultTcs.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
        Assert(result.State == AgentTaskState.Completed,
            $"Expected Completed state, got {result.State}");
        Assert(result.Message is not null, "Result message is null");

        var responseText = result.Message!.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault() ?? "";

        Assert(!string.IsNullOrWhiteSpace(responseText), "Result message text is empty");
    }

    /// <summary>
    /// Scenario 6: Verify that a task with an empty Source is dead-lettered.
    /// </summary>
    public static async Task EmptySourceRejectedAsync(IServiceProvider services, CancellationToken ct)
    {
        var publisher = services.GetRequiredService<IMessagePublisher>();
        var subscriber = services.GetRequiredService<IMessageSubscriber>();

        var taskId = Guid.NewGuid().ToString("N");
        var replyTopic = "agent.response.badcaller";

        // Subscribe to the reply topic — we should NOT receive a response
        var gotResponse = false;
        var subName = $"a2a-test-reject-{Guid.NewGuid():N}";
        await using var sub = await subscriber.SubscribeAsync(
            replyTopic,
            subName,
            (envelope, _) =>
            {
                try
                {
                    var result = envelope.GetPayload<AgentTaskResult>();
                    if (result?.TaskId == taskId) gotResponse = true;
                }
                catch { /* ignore */ }

                try
                {
                    var error = envelope.GetPayload<AgentTaskError>();
                    if (error?.TaskId == taskId) gotResponse = true;
                }
                catch { /* ignore */ }

                return Task.FromResult(MessageResult.Ack);
            },
            ct);

        await Task.Delay(500, ct);

        // Publish a task with empty Source — should be rejected by identity middleware
        var request = new AgentTaskRequest
        {
            TaskId = taskId,
            Skill = "notify-user",
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = "Should be rejected" }]
            }
        };

        // Build envelope manually with empty source
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        var envelope = MessageEnvelope.Create(
            messageType: typeof(AgentTaskRequest).FullName!,
            body: body,
            source: "",
            correlationId: taskId,
            replyTo: replyTopic);

        await publisher.PublishAsync("agent.task.RockBot", envelope, ct);

        // Wait a reasonable time and verify no response arrived
        await Task.Delay(10_000, ct);
        Assert(!gotResponse, "Expected no response for empty-source message, but got one");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
