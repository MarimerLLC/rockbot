using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

[TestClass]
public class AgentProfileExtensionsTests
{
    [TestMethod]
    public void WithProfile_RegistersProfileProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile();
        });

        var provider = services.BuildServiceProvider();
        var profileProvider = provider.GetService<IAgentProfileProvider>();

        Assert.IsNotNull(profileProvider);
    }

    [TestMethod]
    public void WithProfile_RegistersSystemPromptBuilder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile();
        });

        var provider = services.BuildServiceProvider();
        var promptBuilder = provider.GetService<ISystemPromptBuilder>();

        Assert.IsNotNull(promptBuilder);
        Assert.IsInstanceOfType<DefaultSystemPromptBuilder>(promptBuilder);
    }

    [TestMethod]
    public void WithProfile_RegistersHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageSubscriber>(new StubSubscriber());
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile();
        });

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>();

        Assert.IsTrue(
            hostedServices.Any(s => s is AgentProfileLoader),
            "Expected AgentProfileLoader to be registered as IHostedService");
    }

    [TestMethod]
    public void WithProfile_CustomOptions_ConfiguresBasePath()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile(opts => opts.BasePath = "custom-path");
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentProfileOptions>>();

        Assert.AreEqual("custom-path", options.Value.BasePath);
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
