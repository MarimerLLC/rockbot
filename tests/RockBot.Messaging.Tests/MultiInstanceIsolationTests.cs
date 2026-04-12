using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Messaging;
using RockBot.Messaging.InProcess;
using RockBot.Messaging.RabbitMQ;
using RockBot.UserProxy;

namespace RockBot.Messaging.Tests;

/// <summary>
/// Proves that two RockBot agent instances with different names can share the
/// same message broker without cross-contamination. Each agent's proxy publishes
/// and subscribes to agent-name-scoped topics; messages must never leak across.
/// </summary>
[TestClass]
public class MultiInstanceIsolationTests
{
    // ── InProcess tests (always run) ─────────────────────────────────────────

    [TestMethod]
    public async Task InProcess_TwoAgents_MessagesDoNotCrossContaminate()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddRockBotInProcessMessaging();
        await using var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IMessagePublisher>();
        var subscriber = provider.GetRequiredService<IMessageSubscriber>();

        await RunIsolationTest(publisher, subscriber);
    }

    [TestMethod]
    public async Task InProcess_SubagentTopics_DoNotCrossContaminate()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddRockBotInProcessMessaging();
        await using var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IMessagePublisher>();
        var subscriber = provider.GetRequiredService<IMessageSubscriber>();

        await RunSubagentIsolationTest(publisher, subscriber);
    }

    // ── RabbitMQ tests (gated by env var) ────────────────────────────────────

    [TestMethod]
    public async Task RabbitMq_TwoAgents_MessagesDoNotCrossContaminate()
    {
        var host = Environment.GetEnvironmentVariable("ROCKBOT_RABBITMQ_HOST");
        if (string.IsNullOrEmpty(host))
        {
            Assert.Inconclusive("RabbitMQ not available (set ROCKBOT_RABBITMQ_HOST)");
            return;
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddRockBotRabbitMq(opts =>
        {
            opts.HostName = host;
            opts.ExchangeName = $"rockbot-isolation-{Guid.NewGuid():N}";
            opts.DeadLetterExchangeName = $"rockbot-isolation-dlx-{Guid.NewGuid():N}";
        });
        await using var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IMessagePublisher>();
        var subscriber = provider.GetRequiredService<IMessageSubscriber>();

        await RunIsolationTest(publisher, subscriber);
    }

    [TestMethod]
    public async Task RabbitMq_SubagentTopics_DoNotCrossContaminate()
    {
        var host = Environment.GetEnvironmentVariable("ROCKBOT_RABBITMQ_HOST");
        if (string.IsNullOrEmpty(host))
        {
            Assert.Inconclusive("RabbitMQ not available (set ROCKBOT_RABBITMQ_HOST)");
            return;
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddRockBotRabbitMq(opts =>
        {
            opts.HostName = host;
            opts.ExchangeName = $"rockbot-isolation-{Guid.NewGuid():N}";
            opts.DeadLetterExchangeName = $"rockbot-isolation-dlx-{Guid.NewGuid():N}";
        });
        await using var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IMessagePublisher>();
        var subscriber = provider.GetRequiredService<IMessageSubscriber>();

        await RunSubagentIsolationTest(publisher, subscriber);
    }

    // ── Core test logic ──────────────────────────────────────────────────────

    /// <summary>
    /// Two UserProxyService instances (Alpha and Beta) each send a message through
    /// a shared bus. Simulated agent handlers echo replies. We verify each proxy
    /// receives only its own agent's reply, and each agent handler receives only
    /// its own proxy's message.
    /// </summary>
    private static async Task RunIsolationTest(
        IMessagePublisher publisher, IMessageSubscriber subscriber)
    {
        const string alphaName = "AgentAlpha";
        const string betaName = "AgentBeta";

        // Track which agent handlers were invoked and by whom
        var alphaReceived = new List<string>();
        var betaReceived = new List<string>();

        // Simulate Agent Alpha: subscribe to user.message.AgentAlpha, reply on envelope.ReplyTo
        await using var alphaAgent = await SimulateAgent(
            subscriber, publisher, alphaName, alphaReceived);

        // Simulate Agent Beta: subscribe to user.message.AgentBeta, reply on envelope.ReplyTo
        await using var betaAgent = await SimulateAgent(
            subscriber, publisher, betaName, betaReceived);

        // Create two proxies sharing the same bus
        var proxyAlpha = CreateProxy(publisher, subscriber, alphaName, "proxy-alpha");
        var proxyBeta = CreateProxy(publisher, subscriber, betaName, "proxy-beta");

        await proxyAlpha.StartAsync(CancellationToken.None);
        await proxyBeta.StartAsync(CancellationToken.None);

        try
        {
            // Send through each proxy concurrently
            var alphaTask = proxyAlpha.SendAsync(
                new UserMessage { Content = "Hello from Alpha", SessionId = "s-alpha", UserId = "u1" },
                timeout: TimeSpan.FromSeconds(5));
            var betaTask = proxyBeta.SendAsync(
                new UserMessage { Content = "Hello from Beta", SessionId = "s-beta", UserId = "u2" },
                timeout: TimeSpan.FromSeconds(5));

            var alphaReply = await alphaTask;
            var betaReply = await betaTask;

            // Each proxy got a reply
            Assert.IsNotNull(alphaReply, "Proxy Alpha should receive a reply");
            Assert.IsNotNull(betaReply, "Proxy Beta should receive a reply");

            // Each reply came from the correct agent
            Assert.AreEqual(alphaName, alphaReply.AgentName,
                "Alpha's reply should come from AgentAlpha");
            Assert.AreEqual(betaName, betaReply.AgentName,
                "Beta's reply should come from AgentBeta");

            // Each reply echoes the correct content
            Assert.IsTrue(alphaReply.Content.Contains("Hello from Alpha"),
                "Alpha's reply should echo Alpha's message");
            Assert.IsTrue(betaReply.Content.Contains("Hello from Beta"),
                "Beta's reply should echo Beta's message");

            // No cross-contamination: each agent handler was invoked exactly once
            Assert.AreEqual(1, alphaReceived.Count,
                "AgentAlpha handler should receive exactly 1 message");
            Assert.AreEqual(1, betaReceived.Count,
                "AgentBeta handler should receive exactly 1 message");

            // Verify the correct message reached each handler
            Assert.AreEqual("Hello from Alpha", alphaReceived[0]);
            Assert.AreEqual("Hello from Beta", betaReceived[0]);
        }
        finally
        {
            await proxyAlpha.StopAsync(CancellationToken.None);
            await proxyBeta.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Verifies subagent topics are also isolated: subagent.progress.{agentName}
    /// and subagent.result.{agentName} don't cross between agent instances.
    /// </summary>
    private static async Task RunSubagentIsolationTest(
        IMessagePublisher publisher, IMessageSubscriber subscriber)
    {
        const string alphaName = "AgentAlpha";
        const string betaName = "AgentBeta";

        var alphaProgress = new List<string>();
        var betaProgress = new List<string>();
        var alphaResults = new List<string>();
        var betaResults = new List<string>();

        // Subscribe to scoped subagent topics for each agent
        await using var alphaProgressSub = await subscriber.SubscribeAsync(
            $"subagent.progress.{alphaName}", $"progress-{alphaName}",
            (env, _) => { lock (alphaProgress) alphaProgress.Add(env.Source); return Task.FromResult(MessageResult.Ack); });

        await using var betaProgressSub = await subscriber.SubscribeAsync(
            $"subagent.progress.{betaName}", $"progress-{betaName}",
            (env, _) => { lock (betaProgress) betaProgress.Add(env.Source); return Task.FromResult(MessageResult.Ack); });

        await using var alphaResultSub = await subscriber.SubscribeAsync(
            $"subagent.result.{alphaName}", $"result-{alphaName}",
            (env, _) => { lock (alphaResults) alphaResults.Add(env.Source); return Task.FromResult(MessageResult.Ack); });

        await using var betaResultSub = await subscriber.SubscribeAsync(
            $"subagent.result.{betaName}", $"result-{betaName}",
            (env, _) => { lock (betaResults) betaResults.Add(env.Source); return Task.FromResult(MessageResult.Ack); });

        // Publish subagent messages for each agent
        var alphaProgressMsg = MessageEnvelope.Create("SubagentProgressMessage",
            Array.Empty<byte>(), "subagent-alpha-1");
        await publisher.PublishAsync($"subagent.progress.{alphaName}", alphaProgressMsg);

        var betaProgressMsg = MessageEnvelope.Create("SubagentProgressMessage",
            Array.Empty<byte>(), "subagent-beta-1");
        await publisher.PublishAsync($"subagent.progress.{betaName}", betaProgressMsg);

        var alphaResultMsg = MessageEnvelope.Create("SubagentResultMessage",
            Array.Empty<byte>(), "subagent-alpha-1");
        await publisher.PublishAsync($"subagent.result.{alphaName}", alphaResultMsg);

        var betaResultMsg = MessageEnvelope.Create("SubagentResultMessage",
            Array.Empty<byte>(), "subagent-beta-1");
        await publisher.PublishAsync($"subagent.result.{betaName}", betaResultMsg);

        // Wait for delivery
        await Task.Delay(500);

        // Each agent's subscribers received exactly their own messages
        Assert.AreEqual(1, alphaProgress.Count, "Alpha should get 1 progress message");
        Assert.AreEqual("subagent-alpha-1", alphaProgress[0]);

        Assert.AreEqual(1, betaProgress.Count, "Beta should get 1 progress message");
        Assert.AreEqual("subagent-beta-1", betaProgress[0]);

        Assert.AreEqual(1, alphaResults.Count, "Alpha should get 1 result message");
        Assert.AreEqual("subagent-alpha-1", alphaResults[0]);

        Assert.AreEqual(1, betaResults.Count, "Beta should get 1 result message");
        Assert.AreEqual("subagent-beta-1", betaResults[0]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static UserProxyService CreateProxy(
        IMessagePublisher publisher,
        IMessageSubscriber subscriber,
        string agentName,
        string proxyId)
    {
        var options = new UserProxyOptions { ProxyId = proxyId, AgentName = agentName };
        var frontend = new NoOpFrontend();
        return new UserProxyService(
            publisher, subscriber, frontend, options,
            NullLogger<UserProxyService>.Instance);
    }

    /// <summary>
    /// Simulates an agent: subscribes to <c>user.message.{agentName}</c> and publishes
    /// an <see cref="AgentReply"/> back to the envelope's <c>ReplyTo</c> topic.
    /// Records received message content for cross-contamination assertions.
    /// </summary>
    private static async Task<ISubscription> SimulateAgent(
        IMessageSubscriber subscriber,
        IMessagePublisher publisher,
        string agentName,
        List<string> receivedMessages)
    {
        return await subscriber.SubscribeAsync(
            $"{UserProxyTopics.UserMessage}.{agentName}",
            $"agent-handler-{agentName}",
            async (envelope, ct) =>
            {
                var message = envelope.GetPayload<UserMessage>();
                if (message is null) return MessageResult.DeadLetter;

                lock (receivedMessages)
                    receivedMessages.Add(message.Content);

                var reply = new AgentReply
                {
                    Content = $"Echo: {message.Content}",
                    SessionId = message.SessionId,
                    AgentName = agentName,
                    IsFinal = true
                };

                var replyTopic = envelope.ReplyTo;
                if (string.IsNullOrEmpty(replyTopic)) return MessageResult.Ack;

                var replyEnvelope = reply.ToEnvelope<AgentReply>(
                    source: agentName,
                    correlationId: envelope.CorrelationId);

                await publisher.PublishAsync(replyTopic, replyEnvelope, ct);
                return MessageResult.Ack;
            });
    }

    private sealed class NoOpFrontend : IUserFrontend
    {
        public Task DisplayReplyAsync(AgentReply reply, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task DisplayErrorAsync(string message, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
