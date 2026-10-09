using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Llm;

/// <summary>
/// DI registration extensions for the LLM handler.
/// </summary>
public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IModelBehaviorProvider"/> and resolves a <see cref="ModelBehavior"/>
    /// singleton for the currently configured <see cref="IChatClient"/> model.
    /// Consumers can inject <see cref="ModelBehavior"/> directly to get tweaks for the active model.
    /// Uses <c>TryAddSingleton</c> for <see cref="ModelBehavior"/> so that
    /// <see cref="RockBot.Host.ServiceCollectionExtensions.AddRockBotChatClient"/> can
    /// register it first (breaking the circular dependency with <see cref="IChatClient"/>).
    /// </summary>
    public static IServiceCollection AddModelBehaviors(
        this IServiceCollection services,
        Action<ModelBehaviorOptions>? configure = null)
    {
        var options = new ModelBehaviorOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<IModelBehaviorProvider, DefaultModelBehaviorProvider>();

        // TryAdd: if AddRockBotChatClient already registered ModelBehavior (using the
        // inner client's model ID to avoid circular dependency), skip this registration.
        // Otherwise, resolve from IChatClient → model ID → IModelBehaviorProvider.
        services.TryAddSingleton(sp =>
        {
            var client = sp.GetRequiredService<IChatClient>();
            var provider = sp.GetRequiredService<IModelBehaviorProvider>();
            var modelId = client.GetService<ChatClientMetadata>()?.DefaultModelId;
            return provider.GetBehavior(modelId);
        });

        return services;
    }

    /// <summary>
    /// Registers a <see cref="TieredChatClientRegistry"/> backed by three pre-built
    /// <see cref="IChatClient"/> instances (one per tier). Each inner client is optionally
    /// wrapped with <see cref="RockBotFunctionInvokingChatClient"/> based on the resolved
    /// <see cref="ModelBehavior"/> for the <paramref name="balancedInnerClient"/>.
    /// <para>
    /// Also registers <see cref="IChatClient"/> as the balanced client from the registry,
    /// so existing consumers that inject <see cref="IChatClient"/> continue to work.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="lowInnerClient">Pre-built IChatClient for the Low tier.</param>
    /// <param name="balancedInnerClient">Pre-built IChatClient for the Balanced tier.</param>
    /// <param name="highInnerClient">Pre-built IChatClient for the High tier.</param>
    public static IServiceCollection AddRockBotTieredChatClients(
        this IServiceCollection services,
        IChatClient lowInnerClient,
        IChatClient balancedInnerClient,
        IChatClient highInnerClient)
    {
        // Resolve ModelBehavior from the balanced inner client's metadata (before wrapping)
        // to avoid a circular dependency with the registered IChatClient singleton.
        services.TryAddSingleton(sp =>
        {
            var provider = sp.GetService<IModelBehaviorProvider>();
            if (provider is not null)
            {
                var modelId = balancedInnerClient.GetService<ChatClientMetadata>()?.DefaultModelId;
                return provider.GetBehavior(modelId);
            }
            return ModelBehavior.Default;
        });

        // Build the registry lazily so ILogger and ModelBehavior are available.
        services.AddSingleton(sp =>
        {
            var behavior = sp.GetRequiredService<ModelBehavior>();
            var logger = sp.GetRequiredService<ILogger<RockBotFunctionInvokingChatClient>>();
            var diagLogger = sp.GetRequiredService<ILogger<DiagnosticLoggingChatClient>>();
            var progressNotifier = sp.GetService<IToolProgressNotifier>();
            var toolCallLog = sp.GetService<IToolCallLog>();
            var costEstimator = sp.GetRequiredService<LlmCostEstimator>();
            var workingMemory = sp.GetRequiredService<IWorkingMemory>();

            IChatClient Wrap(IChatClient raw, string tierLabel, IChatClient? escalateTo = null)
            {
                // Diagnostic-watch sits between the OpenAI client and the function-invoking
                // middleware so it sees each tool iteration's raw response, including the
                // assistant message before any RockBot post-processing.
                var watched = new DiagnosticLoggingChatClient(raw, diagLogger, tierLabel);
                if (behavior.UseTextBasedToolCalling)
                {
                    // Tools stay in ChatOptions for AgentLoopRunner to dispatch parsed calls.
                    // Only suppress them on the wire when the provider rejects a tools array
                    // outright — see ToolStrippingChatClient. Mid-turn escalation on this path
                    // happens in AgentLoopRunner, which owns every iteration's tier.
                    return behavior.SuppressToolSchemaInRequest
                        ? new ToolStrippingChatClient(watched)
                        : watched;
                }

                // Native path: the function-invoking loop owns the iterations, so the Low
                // tier's escalation to Balanced (#663) has to happen below it.
                IChatClient inner = escalateTo is null
                    ? watched
                    : new TierEscalatingChatClient(watched, escalateTo);
                return new RockBotFunctionInvokingChatClient(inner, progressNotifier, toolCallLog, behavior,
                    costEstimator, workingMemory, sp.GetRequiredService<IOptions<AgentHostOptions>>(), logger);
            }

            // The escalation target is the Balanced tier's raw client (with its own diagnostic
            // watch), not its function-invoking wrapper: the Low tier's loop keeps running and
            // only its model changes.
            var escalationTarget = new DiagnosticLoggingChatClient(balancedInnerClient, diagLogger, "Balanced (escalated)");

            return new TieredChatClientRegistry(
                Wrap(lowInnerClient, "Low", escalateTo: escalationTarget),
                Wrap(balancedInnerClient, "Balanced"),
                Wrap(highInnerClient, "High"));
        });

        // Keep Balanced as the primary IChatClient singleton for consumers that
        // inject IChatClient directly (e.g. ResearchAgentTaskHandler).
        services.AddSingleton<IChatClient>(sp =>
            sp.GetRequiredService<TieredChatClientRegistry>().GetClient(ModelTier.Balanced));

        return services;
    }
}
