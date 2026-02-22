using System.Text.Json;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// Publishes an <see cref="AgentTaskRequest"/> to a target agent and registers the
/// pending task in <see cref="A2ATaskTracker"/>. Returns the task ID immediately.
/// </summary>
internal sealed class InvokeAgentExecutor(
    IMessagePublisher publisher,
    A2ATaskTracker tracker,
    A2AOptions options,
    AgentIdentity identity) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        Dictionary<string, JsonElement> args;
        try
        {
            args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments) ?? [];
        }
        catch
        {
            return Error(request, "Invalid arguments JSON.");
        }

        if (!args.TryGetValue("agent_name", out var agentEl) || agentEl.ValueKind != JsonValueKind.String)
            return Error(request, "Missing required argument: agent_name");

        if (!args.TryGetValue("skill", out var skillEl) || skillEl.ValueKind != JsonValueKind.String)
            return Error(request, "Missing required argument: skill");

        if (!args.TryGetValue("message", out var messageEl) || messageEl.ValueKind != JsonValueKind.String)
            return Error(request, "Missing required argument: message");

        var agentName = agentEl.GetString()!;
        var skill = skillEl.GetString()!;
        var messageText = messageEl.GetString()!;
        int timeoutMinutes = args.TryGetValue("timeout_minutes", out var toEl) && toEl.TryGetInt32(out var to) ? to : 5;

        var taskId = Guid.NewGuid().ToString("N");
        var primarySessionId = request.SessionId ?? "unknown";

        var taskRequest = new AgentTaskRequest
        {
            TaskId = taskId,
            Skill = skill,
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = messageText }]
            }
        };

        var replyTo = $"{options.CallerResultTopic}.{identity.Name}";
        var envelope = taskRequest.ToEnvelope<AgentTaskRequest>(
            source: identity.Name,
            correlationId: taskId,
            replyTo: replyTo);

        await publisher.PublishAsync($"{options.TaskTopic}.{agentName}", envelope, ct);

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
        var pending = new PendingA2ATask
        {
            TaskId = taskId,
            TargetAgent = agentName,
            PrimarySessionId = primarySessionId,
            StartedAt = DateTimeOffset.UtcNow,
            Cts = cts
        };
        tracker.Track(pending);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = $"Task dispatched to agent '{agentName}' with task_id: {taskId}. " +
                      $"The result will arrive asynchronously and fold into the conversation.",
            IsError = false
        };
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}
