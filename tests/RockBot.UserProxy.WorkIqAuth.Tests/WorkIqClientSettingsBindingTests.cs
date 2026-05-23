using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RockBot.Messaging;

namespace RockBot.UserProxy.WorkIqAuth.Tests;

[TestClass]
public class WorkIqClientSettingsBindingTests
{
    [TestMethod]
    public async Task AddWorkIqAuthClient_BindsTenantClientAndScopes()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkIQ:TenantId"] = "00000000-0000-0000-0000-000000000002",
            ["WorkIQ:ClientId"] = "00000000-0000-0000-0000-000000000001",
            ["WorkIQ:Scopes"] = "scope-a, scope-b ,scope-c"
        }).Build();

        var services = BuildServices(config);
        await using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<WorkIqClientSettings>>().Value;

        Assert.AreEqual("00000000-0000-0000-0000-000000000002", opts.TenantId);
        Assert.AreEqual("00000000-0000-0000-0000-000000000001", opts.ClientId);
        CollectionAssert.AreEqual(new[] { "scope-a", "scope-b", "scope-c" }, opts.Scopes);
        Assert.IsTrue(opts.Enabled);
    }

    [TestMethod]
    public async Task AddWorkIqAuthClient_NoConfig_LeavesSettingsDisabled()
    {
        var services = BuildServices(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<WorkIqClientSettings>>().Value;

        Assert.IsFalse(opts.Enabled);
    }

    [TestMethod]
    public async Task AddWorkIqAuthClient_RegistersFlowAndListener()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkIQ:TenantId"] = "tenant",
            ["WorkIQ:ClientId"] = "client"
        }).Build();

        var services = BuildServices(config);
        await using var provider = services.BuildServiceProvider();

        Assert.IsNotNull(provider.GetService<WorkIqDeviceCodeFlow>());
        Assert.IsNotNull(provider.GetService<IWorkIqAuthStatusListener>());
    }

    private static ServiceCollection BuildServices(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessagePublisher, StubMessagePublisher>();
        services.AddSingleton<IMessageSubscriber, StubMessageSubscriber>();
        services.AddWorkIqAuthClient(config);
        return services;
    }
}
