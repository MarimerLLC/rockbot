using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Host.Middleware;

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

        // Register TracingMiddleware first so it's the outermost wrapper
        builder.UseMiddleware<TracingMiddleware>();

        configure(builder);
        builder.Build();

        services.AddTransient<ILlmClient, LlmClient>();
        services.AddTransient<AgentLoopRunner>();
        services.AddScoped<AgentContextBuilder>();
        services.AddSingleton<SessionStartTracker>();
        services.AddSingleton<IUserActivityMonitor, UserActivityMonitor>();
        services.AddSingleton<IMessagePipeline, MessagePipeline>();
        services.AddSingleton<IHostedService, AgentHost>();

        return services;
    }
}
