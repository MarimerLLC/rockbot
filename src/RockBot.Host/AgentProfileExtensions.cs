using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RockBot.Host;

/// <summary>
/// Extension methods for registering the agent profile system.
/// </summary>
public static class AgentProfileExtensions
{
    /// <summary>
    /// Registers the agent profile system with default options (loads from <c>./agent/</c>).
    /// </summary>
    public static AgentHostBuilder WithProfile(this AgentHostBuilder builder)
        => builder.WithProfile(_ => { });

    /// <summary>
    /// Registers the agent profile system with custom options.
    /// </summary>
    public static AgentHostBuilder WithProfile(
        this AgentHostBuilder builder,
        Action<AgentProfileOptions> configure)
    {
        builder.Services.Configure(configure);

        var holder = new ProfileHolder();
        builder.Services.AddSingleton(holder);
        builder.Services.AddSingleton(sp => sp.GetRequiredService<ProfileHolder>().Profile);

        builder.Services.AddSingleton<IAgentProfileProvider, FileAgentProfileProvider>();
        builder.Services.AddSingleton<ISystemPromptBuilder, DefaultSystemPromptBuilder>();

        builder.Services.AddSingleton<AgentProfileLoader>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AgentProfileLoader>());

        return builder;
    }
}
