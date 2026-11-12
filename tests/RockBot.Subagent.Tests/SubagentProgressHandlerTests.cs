using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Subagent.Tests;

/// <summary>Progress relayed to the user is labelled with who sent it (#795).</summary>
[TestClass]
public sealed class SubagentProgressHandlerTests
{
    private static async Task<(string Topic, AgentReply Reply)> RelayAsync(string? kind)
    {
        var publisher = new RecordingPublisher();
        var agent = new AgentIdentity("test-agent");
        // Only the publisher, identity, origin store and logger take part in relaying progress.
        var handler = new SubagentProgressHandler(
            null!, null!, publisher, agent, null!, null!, null!, null!, null!,
            new SessionOriginStore(), NullLogger<SubagentProgressHandler>.Instance);

        var message = new SubagentProgressMessage
        {
            TaskId = "t1",
            SubagentSessionId = kind == ReplySourceKind.Worker ? "worker-t1" : "subagent-t1",
            PrimarySessionId = "session/s1",
            Message = "halfway there",
            Timestamp = DateTimeOffset.UtcNow,
            Kind = kind,
        };
        await handler.HandleAsync(message, new MessageHandlerContext
        {
            Envelope = message.ToEnvelope<SubagentProgressMessage>(source: "x"),
            Agent = agent,
            Services = new ServiceCollection().BuildServiceProvider(),
            CancellationToken = CancellationToken.None,
        });

        var (topic, envelope) = publisher.Published.Single();
        return (topic, envelope.GetPayload<AgentReply>()!);
    }

    [TestMethod]
    public async Task WorkerProgress_IsLabelledAsAWorker()
    {
        var (_, reply) = await RelayAsync(ReplySourceKind.Worker);

        Assert.AreEqual("worker-t1", reply.AgentName);
        Assert.AreEqual(ReplySourceKind.Worker, reply.Source?.Kind);
        Assert.AreEqual("t1", reply.Source?.TaskId);
        Assert.AreEqual("s1", reply.SessionId, "the raw session id, as on completion replies");
        Assert.IsFalse(reply.IsFinal);
    }

    [TestMethod]
    [DataRow(ReplySourceKind.Subagent)]
    [DataRow(null, DisplayName = "older sender without a kind")]
    public async Task SubagentProgress_IsLabelledAsASubagent(string? kind)
    {
        var (topic, reply) = await RelayAsync(kind);

        Assert.AreEqual($"{UserProxyTopics.UserResponse}.test-agent", topic);
        Assert.AreEqual("subagent-t1", reply.AgentName);
        Assert.AreEqual(ReplySourceKind.Subagent, reply.Source?.Kind);
    }

    private sealed class RecordingPublisher : IMessagePublisher
    {
        public List<(string Topic, MessageEnvelope Envelope)> Published { get; } = [];

        public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Published.Add((topic, envelope));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
