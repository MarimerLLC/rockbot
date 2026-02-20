using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Llm.Tests;

[TestClass]
public class ServiceCollectionExtensionsTests
{
    [TestMethod]
    public void AddLlmHandler_RegistersHandlerAndSubscribesToTopic()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(new StubChatClient());
        services.AddSingleton<Messaging.IMessagePublisher>(new TrackingPublisher());

        services.AddRockBotHost(agent => agent
            .WithIdentity("llm-agent")
            .AddLlmHandler());

        var provider = services.BuildServiceProvider();

        // Handler should be registered
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetService<IMessageHandler<LlmRequest>>();
        Assert.IsNotNull(handler);

        // Topic should be subscribed
        var options = provider.GetRequiredService<IOptions<AgentHostOptions>>().Value;
        CollectionAssert.Contains(options.Topics, "llm.request");
    }

    [TestMethod]
    public void AddLlmHandler_RegistersLlmOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(new StubChatClient());
        services.AddSingleton<Messaging.IMessagePublisher>(new TrackingPublisher());

        services.AddRockBotHost(agent => agent
            .WithIdentity("llm-agent")
            .AddLlmHandler(opts =>
            {
                opts.DefaultModelId = "test-model";
                opts.DefaultTemperature = 0.5f;
                opts.DefaultResponseTopic = "custom.response";
            }));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<LlmOptions>();

        Assert.AreEqual("test-model", options.DefaultModelId);
        Assert.AreEqual(0.5f, options.DefaultTemperature);
        Assert.AreEqual("custom.response", options.DefaultResponseTopic);
    }

    [TestMethod]
    public void AddLlmHandler_DefaultOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(new StubChatClient());
        services.AddSingleton<Messaging.IMessagePublisher>(new TrackingPublisher());

        services.AddRockBotHost(agent => agent
            .WithIdentity("llm-agent")
            .AddLlmHandler());

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<LlmOptions>();

        Assert.IsNull(options.DefaultModelId);
        Assert.IsNull(options.DefaultTemperature);
        Assert.AreEqual("llm.response", options.DefaultResponseTopic);
    }

    [TestMethod]
    public void AddLlmHandler_RegistersMessageType()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(new StubChatClient());
        services.AddSingleton<Messaging.IMessagePublisher>(new TrackingPublisher());

        services.AddRockBotHost(agent => agent
            .WithIdentity("llm-agent")
            .AddLlmHandler());

        var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IMessageTypeResolver>();

        // The message type key should be the full type name
        var resolvedType = resolver.Resolve(typeof(LlmRequest).FullName!);
        Assert.IsNotNull(resolvedType);
        Assert.AreEqual(typeof(LlmRequest), resolvedType);
    }
}
