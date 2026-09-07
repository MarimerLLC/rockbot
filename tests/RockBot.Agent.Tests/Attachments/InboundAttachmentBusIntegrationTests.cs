using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Messaging.RabbitMQ;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests.Attachments;

/// <summary>
/// Drives the upload across a real broker: the client-side
/// <see cref="UserProxyService.UploadAttachmentAsync"/> publishes, RabbitMQ delivers, the real
/// <see cref="AttachmentUploadHandler"/> writes the file, and the response is correlated back.
/// </summary>
/// <remarks>
/// The in-process end-to-end test proves the data path; this proves the transport. It exists
/// because routing uploads through the agent puts multi-megabyte payloads on the bus for the
/// first time, and the failure mode — a large <c>byte[]</c> that does not survive JSON
/// serialisation, AMQP framing, or the correlation plumbing — is invisible until someone attaches
/// a real screenshot.
///
/// <para>Set <c>ROCKBOT_RABBITMQ_HOST</c> (and optionally <c>ROCKBOT_RABBITMQ_PORT</c>) to enable;
/// skipped as inconclusive otherwise, matching <c>RabbitMqIntegrationTests</c>.</para>
/// </remarks>
[TestClass]
public class InboundAttachmentBusIntegrationTests
{
    private static readonly string? RabbitHost = Environment.GetEnvironmentVariable("ROCKBOT_RABBITMQ_HOST");
    private const string AgentName = "attach-proof-agent";

    [TestMethod]
    [Timeout(60_000)]
    public async Task Upload_CrossesTheBus_AndTheFileLandsOnTheSharedVolume()
    {
        if (string.IsNullOrEmpty(RabbitHost))
        {
            Assert.Inconclusive("RabbitMQ not available (set ROCKBOT_RABBITMQ_HOST)");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "rockbot-attach-bus", Guid.NewGuid().ToString("N"));
        var storage = new AttachmentStorage(root);
        var exchange = $"rockbot-attach-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddRockBotRabbitMq(opts =>
        {
            opts.HostName = RabbitHost!;
            opts.Port = int.TryParse(Environment.GetEnvironmentVariable("ROCKBOT_RABBITMQ_PORT"), out var p)
                ? p : 5672;
            opts.ExchangeName = exchange;
            opts.DeadLetterExchangeName = $"{exchange}-dlx";
        });

        await using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<IMessagePublisher>();
        var subscriber = provider.GetRequiredService<IMessageSubscriber>();

        // ── The agent side: the real handler, on the real request topic ──────
        var handler = new AttachmentUploadHandler(
            new InboundAttachmentService(storage, NullLogger<InboundAttachmentService>.Instance),
            publisher,
            NullLogger<AttachmentUploadHandler>.Instance);

        await using var agentSubscription = await subscriber.SubscribeAsync(
            $"{UserProxyTopics.AttachmentUploadRequest}.{AgentName}",
            $"attach-proof-{Guid.NewGuid():N}",
            async (envelope, ct) =>
            {
                var request = envelope.GetPayload<AttachmentUploadRequest>()!;
                await handler.HandleAsync(request, new MessageHandlerContext
                {
                    Envelope = envelope,
                    Agent = new AgentIdentity(AgentName),
                    Services = provider,
                    CancellationToken = ct,
                });
                return MessageResult.Ack;
            });

        // ── The client side: the real proxy call ─────────────────────────────
        var proxy = new UserProxyService(
            publisher, subscriber, new NullFrontend(),
            new UserProxyOptions
            {
                ProxyId = $"attach-proof-{Guid.NewGuid():N}",
                AgentName = AgentName,
                DefaultReplyTimeout = TimeSpan.FromSeconds(30),
            },
            NullLogger<UserProxyService>.Instance);

        // A 1.4 MB payload — base64 inflates this to ~1.9 MB inside the envelope, well past any
        // single AMQP frame, so the body genuinely spans frames.
        var original = new byte[1_400_000];
        Random.Shared.NextBytes(original);
        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        pngSignature.CopyTo(original);

        try
        {
            var response = await proxy.UploadAttachmentAsync(new AttachmentUploadRequest
            {
                FileName = "proof.png",
                Mime = "image/png",
                Data = original,
                SessionId = "sess-bus",
                UserId = "user-bus",
            });

            Assert.IsNotNull(response, "No response came back across the bus.");
            Assert.IsTrue(response!.Success, response.Error);
            Assert.AreEqual("proof.png", response.Attachment!.Path);

            var landed = await File.ReadAllBytesAsync(Path.Combine(root, "proof.png"));
            CollectionAssert.AreEqual(original, landed,
                "1.4 MB must cross the broker byte-for-byte — this is the only message that carries bytes.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Upload_RejectedByTheAgent_ReturnsTheReasonToTheClient()
    {
        if (string.IsNullOrEmpty(RabbitHost))
        {
            Assert.Inconclusive("RabbitMQ not available (set ROCKBOT_RABBITMQ_HOST)");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "rockbot-attach-bus", Guid.NewGuid().ToString("N"));
        var exchange = $"rockbot-attach-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddRockBotRabbitMq(opts =>
        {
            opts.HostName = RabbitHost!;
            opts.Port = int.TryParse(Environment.GetEnvironmentVariable("ROCKBOT_RABBITMQ_PORT"), out var p)
                ? p : 5672;
            opts.ExchangeName = exchange;
            opts.DeadLetterExchangeName = $"{exchange}-dlx";
        });

        await using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<IMessagePublisher>();
        var subscriber = provider.GetRequiredService<IMessageSubscriber>();
        var storage = new AttachmentStorage(root);
        var handler = new AttachmentUploadHandler(
            new InboundAttachmentService(storage, NullLogger<InboundAttachmentService>.Instance),
            publisher,
            NullLogger<AttachmentUploadHandler>.Instance);

        await using var agentSubscription = await subscriber.SubscribeAsync(
            $"{UserProxyTopics.AttachmentUploadRequest}.{AgentName}",
            $"attach-proof-{Guid.NewGuid():N}",
            async (envelope, ct) =>
            {
                var request = envelope.GetPayload<AttachmentUploadRequest>()!;
                await handler.HandleAsync(request, new MessageHandlerContext
                {
                    Envelope = envelope,
                    Agent = new AgentIdentity(AgentName),
                    Services = provider,
                    CancellationToken = ct,
                });
                return MessageResult.Ack;
            });

        var proxy = new UserProxyService(
            publisher, subscriber, new NullFrontend(),
            new UserProxyOptions
            {
                ProxyId = $"attach-proof-{Guid.NewGuid():N}",
                AgentName = AgentName,
                DefaultReplyTimeout = TimeSpan.FromSeconds(30),
            },
            NullLogger<UserProxyService>.Instance);

        try
        {
            var response = await proxy.UploadAttachmentAsync(new AttachmentUploadRequest
            {
                FileName = "payload.exe",
                Mime = "application/x-msdownload",
                Data = [1, 2, 3],
                SessionId = "sess-bus",
                UserId = "user-bus",
            });

            Assert.IsNotNull(response);
            Assert.IsFalse(response!.Success);
            StringAssert.Contains(response.Error!, "not accepted",
                "A rejection has to reach the person who picked the file, not just the agent's log.");
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NullFrontend : IUserFrontend
    {
        public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
