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
            b.AddToolHandler();
        });

        Assert.IsTrue(services.Any(sd => sd.ServiceType == typeof(IToolRegistry)));
    }
}
