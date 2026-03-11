using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.ServiceSearch;

/// <summary>
/// DI registration extensions for the unified service search feature.
/// </summary>
public static class ServiceSearchServiceCollectionExtensions
{
    /// <summary>
    /// Registers <c>search_known_services</c> tool and the <see cref="IServiceSearchIndex"/>
    /// that bridges A2A agents and MCP servers into a single BM25-searchable index.
    /// Also enables per-turn service hints in <see cref="AgentContextBuilder"/>.
    /// </summary>
    public static AgentHostBuilder AddServiceSearch(this AgentHostBuilder builder)
    {
        builder.Services.AddSingleton<ServiceSearchIndex>();
        builder.Services.AddSingleton<IServiceSearchIndex>(
            static sp => sp.GetRequiredService<ServiceSearchIndex>());
        builder.Services.AddHostedService<ServiceSearchToolRegistrar>();
        builder.Services.AddSingleton<IToolSkillProvider, ServiceSearchSkillProvider>();
        return builder;
    }
}
