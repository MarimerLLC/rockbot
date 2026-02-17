using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public class AgentHostBuilderTests
{
    [TestMethod]
    public void WithIdentity_RegistersIdentity()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent => agent
            .WithIdentity("my-agent"));

        var provider = services.BuildServiceProvider();
        var identity = provider.GetRequiredService<AgentIdentity>();

        Assert.AreEqual("my-agent", identity.Name);
        Assert.IsFalse(string.IsNullOrEmpty(identity.InstanceId));
    }

    [TestMethod]
    public void WithIdentity_CustomInstanceId()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent => agent
            .WithIdentity("my-agent", "custom-id"));

        var provider = services.BuildServiceProvider();
        var identity = provider.GetRequiredService<AgentIdentity>();

        Assert.AreEqual("my-agent", identity.Name);
        Assert.AreEqual("custom-id", identity.InstanceId);
    }

    [TestMethod]
    public void SubscribeTo_AddsTopics()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent => agent
            .WithIdentity("my-agent")
            .SubscribeTo("agent.task.*")
            .SubscribeTo("llm.response"));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AgentHostOptions>>().Value;

        Assert.AreEqual(2, options.Topics.Count);
        CollectionAssert.Contains(options.Topics, "agent.task.*");
        CollectionAssert.Contains(options.Topics, "llm.response");
    }

    [TestMethod]
    public void HandleMessage_RegistersResolverAndHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent => agent
            .WithIdentity("my-agent")
            .HandleMessage<PingMessage, TestPingHandler>());

        var provider = services.BuildServiceProvider();

        var resolver = provider.GetRequiredService<IMessageTypeResolver>();
        Assert.IsNotNull(resolver.Resolve(typeof(PingMessage).FullName!));

        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetService<IMessageHandler<PingMessage>>();
        Assert.IsNotNull(handler);
        Assert.IsInstanceOfType(handler, typeof(TestPingHandler));
    }

    [TestMethod]
    public void HandleMessage_WithExplicitKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent => agent
            .WithIdentity("my-agent")
            .HandleMessage<PingMessage, TestPingHandler>("custom.ping"));

        var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IMessageTypeResolver>();

        Assert.IsNotNull(resolver.Resolve("custom.ping"));
    }

    private sealed class TestPingHandler : IMessageHandler<PingMessage>
    {
        public Task HandleAsync(PingMessage message, MessageHandlerContext context)
            => Task.CompletedTask;
    }
}
