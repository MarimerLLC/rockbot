using Microsoft.Extensions.DependencyInjection;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

[TestClass]
public class AgentMemoryExtensionsTests
{
    [TestMethod]
    public void WithMemory_RegistersBothMemoryInterfaces()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile();
            agent.WithMemory();
        });

        var provider = services.BuildServiceProvider();

        Assert.IsNotNull(provider.GetService<IConversationMemory>());
        Assert.IsNotNull(provider.GetService<ILongTermMemory>());
    }

    [TestMethod]
    public void WithConversationMemory_RegistersIConversationMemory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithConversationMemory();
        });

        var provider = services.BuildServiceProvider();
        var memory = provider.GetService<IConversationMemory>();

        Assert.IsNotNull(memory);
    }

    [TestMethod]
    public void WithConversationMemory_CustomOptions_Configures()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithConversationMemory(o => o.MaxTurnsPerSession = 100);
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ConversationMemoryOptions>>();

        Assert.AreEqual(100, options.Value.MaxTurnsPerSession);
    }

    [TestMethod]
    public void WithLongTermMemory_RegistersILongTermMemory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile();
            agent.WithLongTermMemory();
        });

        var provider = services.BuildServiceProvider();
        var memory = provider.GetService<ILongTermMemory>();

        Assert.IsNotNull(memory);
    }

    [TestMethod]
    public void WithLongTermMemory_CustomOptions_Configures()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile();
            agent.WithLongTermMemory(o => o.BasePath = "/custom/memory");
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MemoryOptions>>();

        Assert.AreEqual("/custom/memory", options.Value.BasePath);
    }

    [TestMethod]
    public void WithConversationMemory_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithConversationMemory();
        });

        var provider = services.BuildServiceProvider();
        var memory1 = provider.GetRequiredService<IConversationMemory>();
        var memory2 = provider.GetRequiredService<IConversationMemory>();

        Assert.AreSame(memory1, memory2);
    }

    [TestMethod]
    public void WithLongTermMemory_RegistersCapabilityClaimWriter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile();
            agent.WithLongTermMemory();
        });

        var provider = services.BuildServiceProvider();
        var writer = provider.GetService<ICapabilityClaimWriter>();

        Assert.IsNotNull(writer, "Phase 2 requires ICapabilityClaimWriter to be registered alongside ILongTermMemory.");
    }

    [TestMethod]
    public void WithLongTermMemory_CapabilityClaimWriter_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile();
            agent.WithLongTermMemory();
        });

        var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<ICapabilityClaimWriter>();
        var second = provider.GetRequiredService<ICapabilityClaimWriter>();

        Assert.AreSame(first, second);
    }

    [TestMethod]
    public void WithLongTermMemory_RegistersCapabilityClaimVerifier()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile();
            agent.WithLongTermMemory();
        });
        // Verifier needs an IToolRegistry — supply a minimal one for resolution.
        services.AddSingleton<RockBot.Tools.IToolRegistry, EmptyToolRegistry>();

        var provider = services.BuildServiceProvider();
        var verifier = provider.GetService<ICapabilityClaimVerifier>();

        Assert.IsNotNull(verifier, "Phase 2 requires ICapabilityClaimVerifier to be registered alongside ILongTermMemory.");
    }

    private sealed class EmptyToolRegistry : RockBot.Tools.IToolRegistry
    {
        public IReadOnlyList<RockBot.Tools.ToolRegistration> GetTools() => [];
        public RockBot.Tools.IToolExecutor? GetExecutor(string toolName) => null;
        public void Register(RockBot.Tools.ToolRegistration registration, RockBot.Tools.IToolExecutor executor) { }
        public bool Unregister(string toolName) => false;
    }
}
