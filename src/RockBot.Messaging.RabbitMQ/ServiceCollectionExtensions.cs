using Microsoft.Extensions.DependencyInjection;

namespace RockBot.Messaging.RabbitMQ;

/// <summary>
/// DI registration extensions for RabbitMQ messaging.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers RabbitMQ as the messaging provider for RockBot.
    /// </summary>
    public static IServiceCollection AddRockBotRabbitMq(
        this IServiceCollection services,
        Action<RabbitMqOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.Configure<RabbitMqOptions>(_ => { });

        services.AddSingleton<RabbitMqConnectionManager>();
        services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();
        services.AddSingleton<IMessageSubscriber, RabbitMqSubscriber>();

        return services;
    }
}
