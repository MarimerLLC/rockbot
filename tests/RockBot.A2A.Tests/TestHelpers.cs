using System.Text.Json;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A.Tests;

/// <summary>
/// Captures all published envelopes for assertion.
/// </summary>
internal sealed class TrackingPublisher : IMessagePublisher
{
    public List<(string Topic, MessageEnvelope Envelope)> Published { get; } = [];

    public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        Published.Add((topic, envelope));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Shared test helpers.
/// </summary>
internal static class TestEnvelopeHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static MessageEnvelope CreateEnvelope<T>(
        T payload,
        string source = "test-source",
        string? correlationId = null,
        string? replyTo = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return MessageEnvelope.Create(
            messageType: typeof(T).FullName ?? typeof(T).Name,
            body: body,
            source: source,
            correlationId: correlationId,
            replyTo: replyTo);
    }
}

/// <summary>
/// Stub <see cref="IAgentTaskHandler"/> that returns a configurable result.
/// </summary>
internal sealed class StubAgentTaskHandler : IAgentTaskHandler
{
    public AgentTaskResult? ResultToReturn { get; set; }
    public Exception? ExceptionToThrow { get; set; }
    public List<(AgentTaskRequest Request, AgentTaskContext Context)> Invocations { get; } = [];

    public Task<AgentTaskResult> HandleTaskAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        Invocations.Add((request, context));

        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        return Task.FromResult(ResultToReturn ?? new AgentTaskResult
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            State = AgentTaskState.Completed,
            Message = new AgentMessage
            {
                Role = "agent",
                Parts = [new AgentMessagePart { Kind = "text", Text = "Done" }]
            }
        });
    }
}
