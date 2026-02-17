using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RockBot.Host;

/// <summary>
/// DI registration extensions for the RockBot agent host.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the RockBot agent host runtime.
    /// </summary>
    public static IServiceCollection AddRockBotHost(
        this IServiceCollection services,
        Action<AgentHostBuilder> configure)
    {
        var builder = new AgentHostBuilder(services);
        configure(builder);
        builder.Build();

        services.AddSingleton<IMessagePipeline, MessagePipeline>();
        services.AddSingleton<IHostedService, AgentHost>();

        return services;
    }
}
