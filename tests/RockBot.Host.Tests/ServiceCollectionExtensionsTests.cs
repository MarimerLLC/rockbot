using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

[TestClass]
public class ServiceCollectionExtensionsTests
{
    [TestMethod]
    public void AddRockBotHost_RegistersPipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent => agent.WithIdentity("test"));

        var provider = services.BuildServiceProvider();
        var pipeline = provider.GetService<IMessagePipeline>();

        Assert.IsNotNull(pipeline);
    }

    [TestMethod]
    public void AddRockBotHost_RegistersHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageSubscriber>(new StubSubscriber());
        services.AddRockBotHost(agent => agent.WithIdentity("test"));

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>();

        Assert.IsTrue(hostedServices.Any(), "Expected at least one IHostedService");
    }

    [TestMethod]
    public void AddRockBotHost_RegistersIdentity()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent => agent.WithIdentity("my-agent"));

        var provider = services.BuildServiceProvider();
        var identity = provider.GetService<AgentIdentity>();

        Assert.IsNotNull(identity);
        Assert.AreEqual("my-agent", identity.Name);
    }

    [TestMethod]
    public void AddRockBotHost_RegistersTypeResolver()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent => agent.WithIdentity("test"));

        var provider = services.BuildServiceProvider();
        var resolver = provider.GetService<IMessageTypeResolver>();

        Assert.IsNotNull(resolver);
    }

    private sealed class StubSubscriber : IMessageSubscriber
    {
        public Task<ISubscription> SubscribeAsync(string topic, string subscriptionName,
            Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ISubscription>(new StubSubscription(topic, subscriptionName));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSubscription(string topic, string subscriptionName) : ISubscription
    {
        public string Topic => topic;
        public string SubscriptionName => subscriptionName;
        public bool IsActive => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
