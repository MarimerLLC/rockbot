namespace RockBot.Host;

/// <summary>
/// What a migration is being asked to do: the store it belongs to, where that store's data
/// lives on disk, and the version step it is bridging.
/// </summary>
/// <remarks>
/// A migration is handed a <em>path</em>, never the store service that owns it. Migrations run
/// from an <c>IHostedService</c>, and .NET constructs every hosted service before starting any
/// of them, so a migration that resolved its store could observe — or cache — pre-migration
/// data. Reading and rewriting the files directly is the only ordering-safe option.
/// </remarks>
/// <param name="StoreName">The store's stable name, matching <see cref="StoreSchemaDescriptor.StoreName"/>.</param>
/// <param name="StorePath">Absolute path to the store's root directory. Created if it did not exist.</param>
/// <param name="FromVersion">The schema version the on-disk data is currently at.</param>
/// <param name="ToVersion">The schema version the data will be stamped at once this migration returns.</param>
public sealed record SchemaMigrationContext(
    string StoreName,
    string StorePath,
    int FromVersion,
    int ToVersion);
