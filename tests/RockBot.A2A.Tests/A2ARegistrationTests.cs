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
        Assert.IsTrue(options.Value.Topics.Contains("agent.task.my-agent"));
        Assert.IsTrue(options.Value.Topics.Contains("agent.task.cancel.my-agent"));
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
            CancellationToken cancellationToken = default)
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
