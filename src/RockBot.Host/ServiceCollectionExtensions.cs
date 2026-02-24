using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host.Middleware;
using RockBot.Llm;

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
        services.AddSingleton<IAgentWorkSerializer, AgentWorkSerializer>();
        services.AddSingleton<IMessagePipeline, MessagePipeline>();
        services.AddSingleton<IHostedService, AgentHost>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="IChatClient"/> for the agent, optionally wrapping it in
    /// <see cref="RockBotFunctionInvokingChatClient"/> for native structured tool calling.
    /// When <see cref="ModelBehavior.UseTextBasedToolCalling"/> is true, the raw client is
    /// registered as-is and <see cref="AgentLoopRunner"/> handles the manual tool loop.
    /// <para>
    /// Uses <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}"/>
    /// for <see cref="ModelBehavior"/> so callers that pre-register it (e.g. with explicit
    /// overrides) take priority. When no prior registration exists, resolves from
    /// <see cref="IModelBehaviorProvider"/> using the inner client's model ID.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRockBotChatClient(
        this IServiceCollection services,
        IChatClient innerClient)
    {
        // TryAdd: only registers ModelBehavior if not already present.
        // Agents that pre-register ModelBehavior (e.g. ResearchAgent with custom overrides)
        // or use AddModelBehaviors() will keep their existing registration.
        services.TryAddSingleton(sp =>
        {
            var provider = sp.GetService<IModelBehaviorProvider>();
            if (provider is not null)
            {
                var modelId = innerClient.GetService<ChatClientMetadata>()?.DefaultModelId;
                return provider.GetBehavior(modelId);
            }

            // No behavior provider registered — use defaults.
            return ModelBehavior.Default;
        });

        services.AddSingleton<IChatClient>(sp =>
        {
            var behavior = sp.GetRequiredService<ModelBehavior>();
            if (behavior.UseTextBasedToolCalling)
                return innerClient;

            return new RockBotFunctionInvokingChatClient(
                innerClient,
                sp.GetService<IToolProgressNotifier>(),
                behavior,
                sp.GetRequiredService<ILogger<RockBotFunctionInvokingChatClient>>());
        });

        // LlmClient now requires TieredChatClientRegistry. When a single client is
        // configured (non-tiered path), register a registry that uses it for all tiers.
        services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<IChatClient>();
            return new TieredChatClientRegistry(client, client, client);
        });

        return services;
    }
}
