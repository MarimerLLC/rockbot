using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// DI registration extensions for the wisp executor subsystem.
/// </summary>
public static class WispServiceCollectionExtensions
{
    /// <summary>
    /// Adds wisp executor support and the <c>spawn_wisps</c> tool.
    /// </summary>
    public static AgentHostBuilder AddWisps(
        this AgentHostBuilder builder,
        Action<WispOptions>? configure = null)
    {
        var options = new WispOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        builder.Services.AddSingleton<WispExecutor>();
        builder.Services.AddSingleton<IWispExecutionLog, FileWispExecutionLog>();
        builder.Services.AddSingleton<IPrunableLog>(sp => (IPrunableLog)sp.GetRequiredService<IWispExecutionLog>());
        builder.Services.AddHostedService<WispToolRegistrar>();
        builder.Services.AddSingleton<IToolSkillProvider, WispToolSkillProvider>();

        return builder;
    }
}
