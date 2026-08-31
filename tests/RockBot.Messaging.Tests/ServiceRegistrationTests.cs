using Microsoft.Extensions.DependencyInjection;
using RockBot.Messaging;
using RockBot.Messaging.RabbitMQ;

namespace RockBot.Messaging.Tests;

[TestClass]
public class ServiceRegistrationTests
{
    [TestMethod]
    public void AddRockBotRabbitMq_RegistersServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotRabbitMq(opts =>
        {
            opts.HostName = "testhost";
            opts.ExchangeName = "test-exchange";
        });

        var provider = services.BuildServiceProvider();

        var publisher = provider.GetService<IMessagePublisher>();
        var subscriber = provider.GetService<IMessageSubscriber>();

        Assert.IsNotNull(publisher);
        Assert.IsNotNull(subscriber);
        Assert.IsInstanceOfType(publisher, typeof(RabbitMqPublisher));
        Assert.IsInstanceOfType(subscriber, typeof(RabbitMqSubscriber));
    }

    [TestMethod]
    public void AddRockBotRabbitMq_DefaultOptions_Work()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotRabbitMq();

        var provider = services.BuildServiceProvider();

        // Should not throw - defaults are applied
        var publisher = provider.GetService<IMessagePublisher>();
        Assert.IsNotNull(publisher);
    }
}
