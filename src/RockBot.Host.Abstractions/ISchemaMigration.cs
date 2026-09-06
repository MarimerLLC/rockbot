namespace RockBot.Host;

/// <summary>
/// A single forward step in one store's on-disk schema history.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are forward-only and run at startup, blocking, before the host serves any
/// message. Each one bridges exactly one version step; a store that is several versions
/// behind runs its migrations in sequence, and the version marker is re-stamped after each
/// step, so an interrupted upgrade resumes rather than restarting.
/// </para>
/// <para>
/// Only ship a migration for a change a tolerant deserializer cannot absorb. Adding an
/// optional property with a default is an additive change and needs no migration. See
/// <c>design/schema-migrations.md</c>.
/// </para>
/// </remarks>
public interface ISchemaMigration
{
    /// <summary>
    /// The store this migration applies to, matching a registered
    /// <see cref="StoreSchemaDescriptor.StoreName"/>.
    /// </summary>
    string StoreName { get; }

    /// <summary>The schema version this migration reads.</summary>
    int FromVersion { get; }

    /// <summary>
    /// The schema version this migration produces. Must be greater than
    /// <see cref="FromVersion"/>.
    /// </summary>
    int ToVersion { get; }

    /// <summary>
    /// Rewrites the store's on-disk data from <see cref="FromVersion"/> to
    /// <see cref="ToVersion"/>.
    /// </summary>
    /// <remarks>
    /// Throwing aborts host startup and leaves the version marker at
    /// <see cref="FromVersion"/>, so the next start retries this same step. Implementations
    /// should therefore be safe to re-run against partially migrated data.
    /// </remarks>
    Task MigrateAsync(SchemaMigrationContext context, CancellationToken cancellationToken = default);
}
