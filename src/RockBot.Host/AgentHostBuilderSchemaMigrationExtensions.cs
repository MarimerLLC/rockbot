using Microsoft.Extensions.DependencyInjection;

namespace RockBot.Host;

/// <summary>
/// Registration extensions for persisted-store schema versioning and migration.
/// </summary>
/// <remarks>
/// The framework enrols its own stores from the extension method that registers each store.
/// A consumer with its own persisted state uses <see cref="AddStoreSchema(AgentHostBuilder, string, int, Func{IServiceProvider, string}, int)"/>
/// to enrol it and <see cref="AddSchemaMigration{TMigration}(AgentHostBuilder)"/> to supply the
/// migrations. See <c>design/schema-migrations.md</c>.
/// </remarks>
public static class AgentHostBuilderSchemaMigrationExtensions
{
    /// <summary>
    /// Registers a migration. Any number may be registered; the runner picks the one that
    /// bridges each version step a store actually needs.
    /// </summary>
    public static AgentHostBuilder AddSchemaMigration<TMigration>(this AgentHostBuilder builder)
        where TMigration : class, ISchemaMigration
    {
        builder.Services.AddSchemaMigration<TMigration>();
        return builder;
    }

    /// <inheritdoc cref="AddSchemaMigration{TMigration}(AgentHostBuilder)"/>
    public static IServiceCollection AddSchemaMigration<TMigration>(this IServiceCollection services)
        where TMigration : class, ISchemaMigration
    {
        services.AddSingleton<ISchemaMigration, TMigration>();
        return services;
    }

    /// <summary>
    /// Enrols a store in the startup schema check.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="storeName">Stable name for the store, recorded in its version marker.</param>
    /// <param name="currentVersion">The schema version this build expects.</param>
    /// <param name="resolvePath">Resolves the store's root directory from the service provider.</param>
    /// <param name="legacyVersion">
    /// Version to assume for an unmarked store that already holds data. Defaults to 1.
    /// </param>
    public static AgentHostBuilder AddStoreSchema(
        this AgentHostBuilder builder,
        string storeName,
        int currentVersion,
        Func<IServiceProvider, string> resolvePath,
        int legacyVersion = 1)
    {
        builder.Services.AddStoreSchema(storeName, currentVersion, resolvePath, legacyVersion);
        return builder;
    }

    /// <inheritdoc cref="AddStoreSchema(AgentHostBuilder, string, int, Func{IServiceProvider, string}, int)"/>
    public static IServiceCollection AddStoreSchema(
        this IServiceCollection services,
        string storeName,
        int currentVersion,
        Func<IServiceProvider, string> resolvePath,
        int legacyVersion = 1)
    {
        services.AddSingleton(
            new StoreSchemaDescriptor(storeName, currentVersion, resolvePath, legacyVersion));
        return services;
    }

    /// <summary>
    /// Configures the startup schema check — whether it runs, and whether it reports instead
    /// of writing.
    /// </summary>
    public static AgentHostBuilder ConfigureSchemaMigrations(
        this AgentHostBuilder builder,
        Action<SchemaMigrationOptions> configure)
    {
        builder.Services.Configure(configure);
        return builder;
    }
}
