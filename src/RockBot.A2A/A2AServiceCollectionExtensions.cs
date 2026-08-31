using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RockBot.Host;

namespace RockBot.A2A;

/// <summary>
/// DI registration extensions for the A2A protocol layer.
/// </summary>
public static class A2AServiceCollectionExtensions
{
    /// <summary>
    /// Registers the A2A task handlers, discovery service, and agent directory.
    /// The user must separately register their <see cref="IAgentTaskHandler"/> implementation.
    /// </summary>
    public static AgentHostBuilder AddA2A(
        this AgentHostBuilder builder,
        Action<A2AOptions>? configure = null)
    {
        var options = new A2AOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        // HttpClient factory — AgentDirectory uses it to fetch peers' /.well-known/agent-card.json
        // at startup and enrich well-known seed entries. Safe to call whether or not AddA2ACaller
        // also runs; AddHttpClient is idempotent.
        builder.Services.AddHttpClient();

        // Agent directory — guard IHostedService registration with a marker so
        // calling both AddA2A() and AddA2ACaller() doesn't wire StartAsync twice.
        builder.Services.TryAddSingleton<AgentDirectory>();
        builder.Services.TryAddSingleton<IAgentDirectory>(sp => sp.GetRequiredService<AgentDirectory>());
        if (!builder.Services.Any(sd => sd.ServiceType == typeof(AgentDirectoryHostedServiceMarker)))
        {
            builder.Services.AddSingleton<AgentDirectoryHostedServiceMarker>();
            builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                sp => sp.GetRequiredService<AgentDirectory>());
        }

        // Identity verification — default to the claims-forwarding verifier: it honors
        // gateway-verified claims (rb-auth-claims) and falls back to name-based (self-asserted)
        // when none are present. Users can override IAgentIdentityVerifier via DI.
        builder.Services.TryAddSingleton<NameBasedAgentIdentityVerifier>();
        builder.Services.TryAddSingleton<IAgentIdentityVerifier, ClaimsForwardingAgentIdentityVerifier>();

        // Trust store — default to file-backed; users can override via DI
        builder.Services.TryAddSingleton<IAgentTrustStore>(sp =>
            new FileAgentTrustStore(options.TrustStorePath));

        // Identity verification middleware — verifies A2A inbound messages
        builder.UseMiddleware<IdentityVerificationMiddleware>();

        // Summarizer — uses ILlmClient if available, otherwise falls back gracefully
        builder.Services.TryAddSingleton<AgentCardSummarizer>();

        // Skill-handler validator — runs before AgentDiscoveryService so the
        // card it publishes already reflects any auto-populated skills.
        builder.Services.AddSingleton<SkillRegistrationValidator>();
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            sp => sp.GetRequiredService<SkillRegistrationValidator>());

        // Discovery hosted service
        builder.Services.AddSingleton<AgentDiscoveryService>();
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            sp => sp.GetRequiredService<AgentDiscoveryService>());

        // Periodic well-known refresh — re-fetches /.well-known/agent-card.json
        // so peer skill/metadata changes become visible without a pod restart.
        builder.Services.AddSingleton<WellKnownRefreshService>();
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            sp => sp.GetRequiredService<WellKnownRefreshService>());

        // Task request handler on agent.task.{agentName}
        var agentName = builder.Identity.Name;

        builder.HandleMessage<AgentTaskRequest, AgentTaskRequestHandler>();
        builder.SubscribeTo($"{options.TaskTopic}.{agentName}");

        // Cancel handler on agent.task.cancel.{agentName}
        builder.HandleMessage<AgentTaskCancelRequest, AgentTaskCancelHandler>();
        builder.SubscribeTo($"{options.CancelTopic}.{agentName}");

        return builder;
    }
}
