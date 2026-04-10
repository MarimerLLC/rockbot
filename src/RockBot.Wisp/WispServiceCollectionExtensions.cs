using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;

namespace RockBot.Wisp;

/// <summary>
/// DI registration extensions for the wisp executor subsystem.
/// </summary>
public static class WispServiceCollectionExtensions
{
    /// <summary>
    /// Adds wisp executor support and the <c>spawn_wisp</c> tool.
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
        builder.Services.AddHostedService<WispToolRegistrar>();

        return builder;
    }
}
