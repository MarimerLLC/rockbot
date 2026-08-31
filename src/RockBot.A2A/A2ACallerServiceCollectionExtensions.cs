using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// DI registration extensions for the A2A caller side (primary agent invoking external agents).
/// </summary>
public static class A2ACallerServiceCollectionExtensions
{
    /// <summary>
    /// Registers A2A caller tools (<c>invoke_agent</c>, <c>list_known_agents</c>) and
    /// result/error/status handlers that fold external agent responses into the primary
    /// agent's LLM conversation. Reuses <see cref="A2AOptions"/> if already registered
    /// by <c>AddA2A()</c>, otherwise registers a fresh instance.
    /// </summary>
    public static AgentHostBuilder AddA2ACaller(
        this AgentHostBuilder builder,
        Action<A2AOptions>? configure = null)
    {
        // Register A2AOptions (may already be registered by AddA2A — TryAdd avoids double-register)
        var options = new A2AOptions();
        configure?.Invoke(options);
        builder.Services.TryAddSingleton(options);

        // Agent directory — shared with AddA2A if both are called.
        // TryAdd avoids double-registration of the singleton instance, and a marker type
        // guards the IHostedService registration so StartAsync/StopAsync are called once.
        builder.Services.TryAddSingleton<AgentDirectory>();
        builder.Services.TryAddSingleton<IAgentDirectory>(
            sp => sp.GetRequiredService<AgentDirectory>());
        if (!builder.Services.Any(sd => sd.ServiceType == typeof(AgentDirectoryHostedServiceMarker)))
        {
            builder.Services.AddSingleton<AgentDirectoryHostedServiceMarker>();
            builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                sp => sp.GetRequiredService<AgentDirectory>());
        }

        // Summarizer — uses ILlmClient if available, otherwise falls back gracefully
        builder.Services.TryAddSingleton<AgentCardSummarizer>();

        // Discovery hosted service — shared with AddA2A if both are called.
        // Guard on the concrete type: IHostedService has many registrations, so
        // TryAddSingleton<IHostedService> would always be skipped.
        if (!builder.Services.Any(sd => sd.ServiceType == typeof(AgentDiscoveryService)))
        {
            builder.Services.AddSingleton<AgentDiscoveryService>();
            builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                sp => sp.GetRequiredService<AgentDiscoveryService>());
        }

        // HttpClient factory for HTTP-transport agent invocation
        builder.Services.AddHttpClient();

        // Pending task tracker
        builder.Services.AddSingleton<A2ATaskTracker>();

        // Session-scoped A2A cancellation seam. Wisp code (which doesn't reference
        // RockBot.A2A) uses this to cancel any in-flight A2A tasks dispatched by a
        // wisp that then failed locally — preventing duplicate remote execution
        // when the LLM retries the wisp.
        builder.Services.AddSingleton<ISessionA2ACanceller, A2ATaskCanceller>();

        // Session-scoped A2A wait seam. Subagent code (which doesn't reference
        // RockBot.A2A) uses this to block before publishing its final result so a
        // late A2A response isn't dropped against a subagent session that has
        // already exited.
        builder.Services.AddSingleton<ISessionA2AAwaiter, A2ATaskAwaiter>();

        // InputRequired handler for multi-turn follow-up (trust-gated LLM response generation)
        builder.Services.AddSingleton<InputRequiredHandler>();

        // Late-reply fold-back: recovers a terminated subagent's primary session so an A2A
        // reply that arrives after the subagent exits is surfaced rather than dropped.
        builder.Services.AddSingleton<A2ALateReplyFolder>();

        // Message handlers for result/error/status + late-reply fold-back
        builder.HandleMessage<AgentTaskResult, A2ATaskResultHandler>();
        builder.HandleMessage<AgentTaskError, A2ATaskErrorHandler>();
        builder.HandleMessage<AgentTaskStatusUpdate, A2ATaskStatusHandler>();
        builder.HandleMessage<LateA2ANotificationMessage, LateA2ANotificationHandler>();

        // Subscribe to the per-agent result topic (agent.response.{agentName}), the shared
        // status topic, and the per-agent late-notification topic
        var agentName = builder.Identity.Name;
        var resultTopic = $"{options.CallerResultTopic}.{agentName}";
        builder.SubscribeTo(resultTopic);
        builder.SubscribeTo(options.StatusTopic);
        builder.SubscribeTo($"{options.LateNotificationTopic}.{agentName}");

        // Tool registration hosted service
        builder.Services.AddHostedService<A2ACallerToolRegistrar>();

        // Skill guide
        builder.Services.AddSingleton<IToolSkillProvider, A2ACallerSkillProvider>();

        return builder;
    }
}
