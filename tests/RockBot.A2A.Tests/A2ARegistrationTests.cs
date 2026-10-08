using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A.Tests;

[TestClass]
public class A2ARegistrationTests
{
    [TestMethod]
    public void AddA2A_RegistersAgentDirectory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessagePublisher, TrackingPublisher>();
        services.AddSingleton<IMessageSubscriber, StubSubscriber>();
        services.AddRockBotHost(agent => agent
            .WithIdentity("test-agent")
            .AddA2A());
        services.AddScoped<IAgentTaskHandler, StubAgentTaskHandler>();

        var provider = services.BuildServiceProvider();

        var directory = provider.GetService<IAgentDirectory>();
        Assert.IsNotNull(directory);
    }

    [TestMethod]
    public void AddA2A_RegistersA2AOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessagePublisher, TrackingPublisher>();
        services.AddSingleton<IMessageSubscriber, StubSubscriber>();
        services.AddRockBotHost(agent => agent
            .WithIdentity("test-agent")
            .AddA2A(opts => opts.StatusTopic = "custom.status"));
        services.AddScoped<IAgentTaskHandler, StubAgentTaskHandler>();

        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<A2AOptions>();
        Assert.AreEqual("custom.status", options.StatusTopic);
    }

    [TestMethod]
    public void AddA2A_RegistersDiscoveryHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessagePublisher, TrackingPublisher>();
        services.AddSingleton<IMessageSubscriber, StubSubscriber>();
        services.AddRockBotHost(agent => agent
            .WithIdentity("test-agent")
            .AddA2A());
        services.AddScoped<IAgentTaskHandler, StubAgentTaskHandler>();

        var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>();
        Assert.IsTrue(hostedServices.Any(s => s is AgentDiscoveryService));
    }

    [TestMethod]
    public void AddA2A_RegistersTopicSubscriptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessagePublisher, TrackingPublisher>();
        services.AddSingleton<IMessageSubscriber, StubSubscriber>();
        services.AddRockBotHost(agent => agent
            .WithIdentity("my-agent")
            .AddA2A());
        services.AddScoped<IAgentTaskHandler, StubAgentTaskHandler>();

        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AgentHostOptions>>();
        Assert.IsTrue(options.Value.Topics.Any(t => t.Topic == "agent.task.my-agent"));
        Assert.IsTrue(options.Value.Topics.Any(t => t.Topic == "agent.task.cancel.my-agent"));
    }

    [TestMethod]
    public void AddA2ACaller_SubscribesToStatusTopicWithRetention()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessagePublisher, TrackingPublisher>();
        services.AddSingleton<IMessageSubscriber, StubSubscriber>();
        services.AddRockBotHost(agent => agent
            .WithIdentity("my-agent")
            .AddA2ACaller());

        var provider = services.BuildServiceProvider();
        var topics = provider.GetRequiredService<IOptions<AgentHostOptions>>().Value.Topics;

        var status = topics.Single(t => t.Topic == new A2AOptions().StatusTopic);
        Assert.AreSame(A2ACallerServiceCollectionExtensions.StatusSubscriptionOptions, status.Options);

        // Per-agent topics are point-to-point and keep the defaults.
        Assert.IsTrue(topics.Where(t => t != status).All(t => t.Options is null));
    }

    [TestMethod]
    public void StatusSubscription_ExpiresStaleUpdatesAndIdleQueues()
    {
        var options = A2ACallerServiceCollectionExtensions.StatusSubscriptionOptions;

        Assert.AreEqual(TimeSpan.FromMinutes(10), options.MessageTtl);
        Assert.AreEqual(TimeSpan.FromHours(24), options.IdleExpiry);
        Assert.IsFalse(options.DeadLetter, "Expired status updates would otherwise fill the DLQ");
        Assert.IsFalse(options.Ephemeral,
            "Deleting the shared queue on dispose would break the replacement pod in a rolling restart");
    }

    [TestMethod]
    public void AddA2A_RegistersMessageTypeResolver()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessagePublisher, TrackingPublisher>();
        services.AddSingleton<IMessageSubscriber, StubSubscriber>();
        services.AddRockBotHost(agent => agent
            .WithIdentity("test-agent")
            .AddA2A());
        services.AddScoped<IAgentTaskHandler, StubAgentTaskHandler>();

        var provider = services.BuildServiceProvider();

        var resolver = provider.GetRequiredService<IMessageTypeResolver>();
        Assert.IsNotNull(resolver.Resolve(typeof(AgentTaskRequest).FullName!));
        Assert.IsNotNull(resolver.Resolve(typeof(AgentTaskCancelRequest).FullName!));
    }

    /// <summary>
    /// Stub subscriber for DI registration tests that don't need real messaging.
    /// </summary>
    private sealed class StubSubscriber : IMessageSubscriber
    {
        public Task<ISubscription> SubscribeAsync(
            string topic, string subscriptionName,
            Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
            CancellationToken cancellationToken = default,
            int dispatchConcurrency = 1)
        {
            return Task.FromResult<ISubscription>(new StubSubscription());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSubscription : ISubscription
    {
        public string Topic => string.Empty;
        public string SubscriptionName => string.Empty;
        public bool IsActive => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
