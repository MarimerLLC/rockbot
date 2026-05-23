using Microsoft.Extensions.DependencyInjection;
using RockBot.UserProxy.Cli;
using RockBot.UserProxy.WorkIqAuth;

namespace RockBot.Cli.Tests;

[TestClass]
public sealed class WorkIqAuthCommandWiringTests
{
    [TestMethod]
    public void HostFactory_WithWorkIqConfigure_ResolvesFlowAndListener()
    {
        // The CLI command passes `builder => builder.Services.AddWorkIqAuthClient(builder.Configuration)`
        // to HostFactory.Build. Verify the resulting host can resolve the flow
        // service the command depends on, without actually starting the host
        // (which would require a real RabbitMQ).
        var settings = new CommonSettings.Plain
        {
            RabbitMqHost = "localhost",
            RabbitMqUser = "test",
            RabbitMqPassword = "test"
        };

        using var host = HostFactory.Build(
            settings,
            useRichFrontend: true,
            configure: b => b.Services.AddWorkIqAuthClient(b.Configuration));

        Assert.IsNotNull(host.Services.GetService<WorkIqDeviceCodeFlow>());
        Assert.IsNotNull(host.Services.GetService<IWorkIqAuthStatusListener>());
    }

    [TestMethod]
    public void HostFactory_WithoutWorkIqConfigure_DoesNotRegisterFlow()
    {
        // Other commands (chat, info, etc.) should not pay for WorkIQ
        // registration. Verify the flow service is absent unless the
        // configure delegate explicitly adds it.
        var settings = new CommonSettings.Plain
        {
            RabbitMqHost = "localhost",
            RabbitMqUser = "test",
            RabbitMqPassword = "test"
        };

        using var host = HostFactory.Build(settings, useRichFrontend: true);

        Assert.IsNull(host.Services.GetService<WorkIqDeviceCodeFlow>());
    }
}
