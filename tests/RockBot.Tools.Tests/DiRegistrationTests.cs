using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Tests;

[TestClass]
public class DiRegistrationTests
{
    [TestMethod]
    public void AddToolHandler_RegistersRequiredServices()
    {
        var services = new ServiceCollection();

        services.AddRockBotHost(b =>
        {
            b.AddToolHandler(opts => opts.DefaultResultTopic = "custom.result");
        });

        // Check that IToolRegistry is registered
        Assert.IsTrue(services.Any(sd => sd.ServiceType == typeof(IToolRegistry)));

        // Check that ToolOptions is registered with correct values
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<ToolOptions>();
        Assert.IsNotNull(options);
        Assert.AreEqual("custom.result", options.DefaultResultTopic);
    }
}
