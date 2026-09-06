using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// DI registration extensions for the wisp executor subsystem.
/// </summary>
public static class WispServiceCollectionExtensions
{
    /// <summary>Schema store name for the wisp execution log.</summary>
    public const string WispStoreSchemaName = "wisp";

    // Bump alongside a migration that changes the log's on-disk shape. Additive changes —
    // a new optional property with a default — leave this alone. See design/schema-migrations.md.
    private const int WispStoreSchemaVersion = 1;

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

        builder.Services.AddSingleton<WispDispatchCircuitBreaker>();
        builder.Services.AddSingleton<WispExecutor>();
        builder.Services.AddSingleton<IWispExecutionLog, FileWispExecutionLog>();

        // The log shares its directory with whatever else uses the shared volume, so the marker
        // carries the store name and the runner skips on a mismatch rather than migrating blind.
        builder.AddStoreSchema(
            WispStoreSchemaName,
            WispStoreSchemaVersion,
            sp => FileWispExecutionLog.ResolvePath(sp.GetRequiredService<WispOptions>()));

        builder.Services.AddSingleton<IPrunableLog>(sp => (IPrunableLog)sp.GetRequiredService<IWispExecutionLog>());
        builder.Services.AddHostedService<WispToolRegistrar>();
        builder.Services.AddSingleton<IToolSkillProvider, WispToolSkillProvider>();

        return builder;
    }
}
