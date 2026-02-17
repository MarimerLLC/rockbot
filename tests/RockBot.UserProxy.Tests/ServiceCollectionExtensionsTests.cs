using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RockBot.UserProxy.Tests;

[TestClass]
public sealed class ServiceCollectionExtensionsTests
{
    [TestMethod]
    public void AddUserProxy_RegistersOptionsWithDefaults()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStubDeps>(_ => null!); // placeholder
        services.AddUserProxy();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<UserProxyOptions>();

        Assert.AreEqual("user-proxy", options.ProxyId);
        Assert.AreEqual(TimeSpan.FromSeconds(60), options.DefaultReplyTimeout);
    }

    [TestMethod]
    public void AddUserProxy_RegistersOptionsWithCustomValues()
    {
        var services = new ServiceCollection();
        services.AddUserProxy(opts =>
        {
            opts.ProxyId = "custom-proxy";
            opts.DefaultReplyTimeout = TimeSpan.FromSeconds(30);
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<UserProxyOptions>();

        Assert.AreEqual("custom-proxy", options.ProxyId);
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.DefaultReplyTimeout);
    }

    [TestMethod]
    public void AddUserProxy_RegistersUserProxyService()
    {
        var services = new ServiceCollection();
        services.AddUserProxy();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(UserProxyService));
        Assert.IsNotNull(descriptor);
        Assert.AreEqual(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [TestMethod]
    public void AddUserProxy_RegistersHostedService()
    {
        var services = new ServiceCollection();
        services.AddUserProxy();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IHostedService));
        Assert.IsNotNull(descriptor);
    }

    // Marker interface for test purposes
    private interface IStubDeps;
}
