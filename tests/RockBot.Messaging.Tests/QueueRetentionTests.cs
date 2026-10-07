using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RockBot.Messaging;
using RockBot.Messaging.InProcess;
using RockBot.Messaging.RabbitMQ;

namespace RockBot.Messaging.Tests;

/// <summary>
/// Unit tests for DLQ retention and ephemeral-queue arguments (issue #606).
/// </summary>
[TestClass]
public class QueueRetentionTests
{
    [TestMethod]
    public void DlqArguments_Shared_HaveRetentionCapsAndNoExpiry()
    {
        var args = RabbitMqSubscriber.BuildDlqArguments(new RabbitMqOptions(), ephemeral: false);

        Assert.IsNotNull(args);
        Assert.AreEqual(259_200_000L, args["x-message-ttl"]);
        Assert.AreEqual(10_000L, args["x-max-length"]);
        Assert.AreEqual(256L * 1024 * 1024, args["x-max-length-bytes"]);
        Assert.AreEqual("drop-head", args["x-overflow"]);
        Assert.IsFalse(args.ContainsKey("x-expires"),
            "A shared DLQ has no consumer; x-expires would delete it while still in use");
    }

    [TestMethod]
    public void DlqArguments_Ephemeral_AddIdleExpiry()
    {
        var options = new RabbitMqOptions { EphemeralQueueExpiry = TimeSpan.FromHours(2) };

        var args = RabbitMqSubscriber.BuildDlqArguments(options, ephemeral: true);

        Assert.IsNotNull(args);
        Assert.AreEqual(7_200_000L, args["x-expires"]);
        Assert.AreEqual(10_000L, args["x-max-length"]);
    }

    [TestMethod]
    public void DlqArguments_RetentionDisabled_Shared_AreNull()
    {
        var options = new RabbitMqOptions();
        options.DlqRetention.Enabled = false;

        Assert.IsNull(RabbitMqSubscriber.BuildDlqArguments(options, ephemeral: false));
    }

    [TestMethod]
    public void DlqArguments_RetentionDisabled_Ephemeral_OnlyExpiry()
    {
        var options = new RabbitMqOptions();
        options.DlqRetention.Enabled = false;

        var args = RabbitMqSubscriber.BuildDlqArguments(options, ephemeral: true);

        Assert.IsNotNull(args);
        Assert.AreEqual(1, args.Count);
        Assert.IsTrue(args.ContainsKey("x-expires"));
    }

    [TestMethod]
    public void QueueArguments_Shared_OnlyDeadLetterRouting()
    {
        var args = RabbitMqSubscriber.BuildQueueArguments(new RabbitMqOptions(), "agent.task", ephemeral: false);

        Assert.AreEqual(2, args.Count);
        Assert.AreEqual("rockbot.dlx", args["x-dead-letter-exchange"]);
        Assert.AreEqual("agent.task", args["x-dead-letter-routing-key"]);
    }

    [TestMethod]
    public void QueueArguments_Ephemeral_AddExpiryAndMessageTtl()
    {
        var args = RabbitMqSubscriber.BuildQueueArguments(new RabbitMqOptions(), "user.response", ephemeral: true);

        Assert.AreEqual(24L * 3_600_000, args["x-expires"]);
        Assert.AreEqual(3_600_000L, args["x-message-ttl"]);
        Assert.AreEqual("rockbot.dlx", args["x-dead-letter-exchange"]);
    }

    [TestMethod]
    public void RetentionOptions_BindFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:DlqRetention:Enabled"] = "false",
                ["RabbitMq:DlqRetention:MessageTtlMs"] = "1000",
                ["RabbitMq:DlqRetention:MaxLength"] = "5",
                ["RabbitMq:DlqRetention:MaxLengthBytes"] = "4096",
                ["RabbitMq:EphemeralQueueExpiry"] = "00:30:00",
                ["RabbitMq:EphemeralMessageTtl"] = "00:05:00",
            })
            .Build();

        var options = new RabbitMqOptions();
        config.GetSection("RabbitMq").Bind(options);

        Assert.IsFalse(options.DlqRetention.Enabled);
        Assert.AreEqual(1000L, options.DlqRetention.MessageTtlMs);
        Assert.AreEqual(5L, options.DlqRetention.MaxLength);
        Assert.AreEqual(4096L, options.DlqRetention.MaxLengthBytes);
        Assert.AreEqual(TimeSpan.FromMinutes(30), options.EphemeralQueueExpiry);
        Assert.AreEqual(TimeSpan.FromMinutes(5), options.EphemeralMessageTtl);
    }

    [TestMethod]
    public async Task SubscriptionOptionsOverload_DefaultImplementation_ForwardsToBasicOverload()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotInProcessMessaging();
        await using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<IMessagePublisher>();
        var subscriber = provider.GetRequiredService<IMessageSubscriber>();

        var received = new TaskCompletionSource<MessageEnvelope>();
        await using var subscription = await subscriber.SubscribeAsync(
            "test.ephemeral",
            "ephemeral-sub",
            (env, _) =>
            {
                received.TrySetResult(env);
                return Task.FromResult(MessageResult.Ack);
            },
            new SubscriptionOptions { Ephemeral = true });

        await publisher.PublishAsync("test.ephemeral", "hello".ToEnvelope("test"));

        var env = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("hello", env.GetPayload<string>());
    }
}

/// <summary>
/// RabbitMQ-backed retention/expiry tests. Set ROCKBOT_RABBITMQ_HOST to run.
/// </summary>
[TestClass]
public class QueueRetentionIntegrationTests
{
    private ServiceProvider? _provider;
    private string? _host;

    [TestInitialize]
    public void Initialize()
    {
        _host = Environment.GetEnvironmentVariable("ROCKBOT_RABBITMQ_HOST");
        if (string.IsNullOrEmpty(_host)) return;

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddRockBotRabbitMq(opts =>
        {
            opts.HostName = _host;
            opts.ExchangeName = $"rockbot-test-{Guid.NewGuid():N}";
            opts.DeadLetterExchangeName = $"rockbot-test-dlx-{Guid.NewGuid():N}";
        });
        _provider = services.BuildServiceProvider();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private bool RequireRabbit()
    {
        if (_provider is not null) return true;
        Assert.Inconclusive("RabbitMQ not available (set ROCKBOT_RABBITMQ_HOST)");
        return false;
    }

    private static Task<MessageResult> Ack(MessageEnvelope _, CancellationToken __) =>
        Task.FromResult(MessageResult.Ack);

    /// <summary>True when the queue exists with exactly <paramref name="args"/> (a mismatch is a 406).</summary>
    private async Task<bool> QueueMatchesAsync(string queue, IDictionary<string, object?>? args)
    {
        var channel = await _provider!.GetRequiredService<RabbitMqConnectionManager>().CreateChannelAsync();
        try
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: args);
            return true;
        }
        catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException ex)
            when (ex.ShutdownReason?.ReplyCode == 406)
        {
            return false;
        }
        finally
        {
            if (channel.IsOpen) await channel.CloseAsync();
        }
    }

    private async Task<bool> QueueExistsAsync(string queue)
    {
        var channel = await _provider!.GetRequiredService<RabbitMqConnectionManager>().CreateChannelAsync();
        try
        {
            await channel.QueueDeclarePassiveAsync(queue);
            return true;
        }
        catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException ex)
            when (ex.ShutdownReason?.ReplyCode == 404)
        {
            return false;
        }
        finally
        {
            if (channel.IsOpen) await channel.CloseAsync();
        }
    }

    private async Task DeleteQueueAsync(string queue)
    {
        var channel = await _provider!.GetRequiredService<RabbitMqConnectionManager>().CreateChannelAsync();
        await channel.QueueDeleteAsync(queue);
        await channel.CloseAsync();
    }

    [TestMethod]
    public async Task SharedSubscription_DeclaresCappedDlq()
    {
        if (!RequireRabbit()) return;
        var name = $"retention-{Guid.NewGuid():N}";
        var subscriber = _provider!.GetRequiredService<IMessageSubscriber>();

        await using (await subscriber.SubscribeAsync("test.retention", name, Ack)) { }

        var expected = RabbitMqSubscriber.BuildDlqArguments(new RabbitMqOptions(), ephemeral: false);
        Assert.IsTrue(await QueueMatchesAsync($"rockbot.{name}.dlq", expected));
        Assert.IsTrue(await QueueExistsAsync($"rockbot.{name}"), "Shared queues survive dispose");

        await DeleteQueueAsync($"rockbot.{name}");
        await DeleteQueueAsync($"rockbot.{name}.dlq");
    }

    [TestMethod]
    public async Task EphemeralSubscription_QueuesDeletedOnDispose()
    {
        if (!RequireRabbit()) return;
        var name = $"ephemeral-{Guid.NewGuid():N}";
        var subscriber = _provider!.GetRequiredService<IMessageSubscriber>();

        var subscription = await subscriber.SubscribeAsync(
            "test.ephemeral", name, Ack, new SubscriptionOptions { Ephemeral = true });
        Assert.IsTrue(await QueueExistsAsync($"rockbot.{name}"));

        await subscription.DisposeAsync();

        Assert.IsFalse(await QueueExistsAsync($"rockbot.{name}"));
        Assert.IsFalse(await QueueExistsAsync($"rockbot.{name}.dlq"));
    }

    [TestMethod]
    public async Task LegacyEmptyDlq_IsMigratedToCappedArguments()
    {
        if (!RequireRabbit()) return;
        var name = $"legacy-empty-{Guid.NewGuid():N}";
        var dlq = $"rockbot.{name}.dlq";
        Assert.IsTrue(await QueueMatchesAsync(dlq, null)); // pre-#606 shape: no arguments

        var subscriber = _provider!.GetRequiredService<IMessageSubscriber>();
        await using (await subscriber.SubscribeAsync("test.legacy", name, Ack)) { }

        var expected = RabbitMqSubscriber.BuildDlqArguments(new RabbitMqOptions(), ephemeral: false);
        Assert.IsTrue(await QueueMatchesAsync(dlq, expected));

        await DeleteQueueAsync($"rockbot.{name}");
        await DeleteQueueAsync(dlq);
    }

    [TestMethod]
    public async Task LegacyNonEmptyDlq_IsKeptAndSubscriptionStillWorks()
    {
        if (!RequireRabbit()) return;
        var name = $"legacy-full-{Guid.NewGuid():N}";
        var dlq = $"rockbot.{name}.dlq";
        Assert.IsTrue(await QueueMatchesAsync(dlq, null));

        var cm = _provider!.GetRequiredService<RabbitMqConnectionManager>();
        var ch = await cm.CreateChannelAsync();
        await ch.BasicPublishAsync(string.Empty, dlq, "dead"u8.ToArray());
        await ch.CloseAsync();

        var subscriber = _provider.GetRequiredService<IMessageSubscriber>();
        var publisher = _provider.GetRequiredService<IMessagePublisher>();
        var received = new TaskCompletionSource();
        await using var subscription = await subscriber.SubscribeAsync("test.legacyfull", name, (_, _) =>
        {
            received.TrySetResult();
            return Task.FromResult(MessageResult.Ack);
        });

        await publisher.PublishAsync("test.legacyfull", "ping".ToEnvelope("test"));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(await QueueMatchesAsync(dlq, null), "A non-empty legacy DLQ must be left untouched");

        await subscription.DisposeAsync();
        await DeleteQueueAsync($"rockbot.{name}");
        await DeleteQueueAsync(dlq);
    }
}
