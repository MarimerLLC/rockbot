using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;

namespace RockBot.Llm;

/// <summary>
/// DI registration extensions for the LLM handler.
/// </summary>
public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers the LLM request handler and subscribes to the "llm.request" topic.
    /// Requires an <see cref="Microsoft.Extensions.AI.IChatClient"/> to be registered separately.
    /// </summary>
    public static AgentHostBuilder AddLlmHandler(
        this AgentHostBuilder builder,
        Action<LlmOptions>? configure = null)
    {
        var options = new LlmOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        builder.HandleMessage<LlmRequest, LlmRequestHandler>();
        builder.SubscribeTo("llm.request");

        return builder;
    }

    /// <summary>
    /// Registers <see cref="IModelBehaviorProvider"/> and resolves a <see cref="ModelBehavior"/>
    /// singleton for the currently configured <see cref="IChatClient"/> model.
    /// Consumers can inject <see cref="ModelBehavior"/> directly to get tweaks for the active model.
    /// </summary>
    public static IServiceCollection AddModelBehaviors(
        this IServiceCollection services,
        Action<ModelBehaviorOptions>? configure = null)
    {
        var options = new ModelBehaviorOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<IModelBehaviorProvider, DefaultModelBehaviorProvider>();

        // Resolve the ModelBehavior for the configured model once at startup.
        // Consumers inject ModelBehavior directly — no need to thread the model ID through call sites.
        services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<IChatClient>();
            var provider = sp.GetRequiredService<IModelBehaviorProvider>();
            var modelId = client.GetService<ChatClientMetadata>()?.DefaultModelId;
            return provider.GetBehavior(modelId);
        });

        return services;
    }
}
