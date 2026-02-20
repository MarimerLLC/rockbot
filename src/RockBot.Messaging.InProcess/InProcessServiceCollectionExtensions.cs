using Microsoft.Extensions.DependencyInjection;
using RockBot.Messaging;

namespace RockBot.Messaging.InProcess;

public static class InProcessServiceCollectionExtensions
{
    public static IServiceCollection AddRockBotInProcessMessaging(this IServiceCollection services)
    {
        services.AddSingleton<InProcessBus>();
        services.AddSingleton<IMessagePublisher, InProcessPublisher>();
        services.AddSingleton<IMessageSubscriber, InProcessSubscriber>();
        return services;
    }
}
