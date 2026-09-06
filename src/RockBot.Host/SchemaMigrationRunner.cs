using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Brings every enrolled store's on-disk data up to the schema version the running code
/// expects, before the host starts serving.
/// </summary>
/// <remarks>
/// See <c>design/schema-migrations.md</c> for the policy this implements and for when a
/// change needs a migration at all.
/// </remarks>
internal sealed class SchemaMigrationRunner
{
    private readonly IReadOnlyList<StoreSchemaDescriptor> _descriptors;
    private readonly IReadOnlyList<ISchemaMigration> _migrations;
    private readonly SchemaMigrationOptions _options;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<SchemaMigrationRunner> _logger;

    public SchemaMigrationRunner(
        IEnumerable<StoreSchemaDescriptor> descriptors,
        IEnumerable<ISchemaMigration> migrations,
        IOptions<SchemaMigrationOptions> options,
        IServiceProvider services,
        ILogger<SchemaMigrationRunner> logger,
        TimeProvider? timeProvider = null)
    {
        _descriptors = descriptors.ToList();
        _migrations = migrations.ToList();
        _options = options.Value;
        _services = services;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning(
                "Schema migration check disabled; {Count} store(s) left unverified", _descriptors.Count);
            return;
        }

        foreach (var descriptor in _descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await MigrateStoreAsync(descriptor, cancellationToken);
        }
    }

    private async Task MigrateStoreAsync(StoreSchemaDescriptor descriptor, CancellationToken cancellationToken)
    {
        var storePath = descriptor.ResolvePath(_services);
        var marker = await StoreSchemaMarkerFile.ReadAsync(storePath, _logger, cancellationToken);

        if (marker is not null &&
            !string.Equals(marker.Store, descriptor.StoreName, StringComparison.Ordinal))
        {
            // Two stores rooted at the same directory. Migrating either one against the other's
            // marker would rewrite data the migration was never written for.
            _logger.LogWarning(
                "Schema marker at {Path} belongs to store '{Found}', not '{Expected}'; skipping migration",
                StoreSchemaMarkerFile.PathFor(storePath), marker.Store, descriptor.StoreName);
            return;
        }

        if (marker is null)
        {
            var isLegacy = StoreSchemaMarkerFile.HasData(storePath);
            var assumed = isLegacy ? descriptor.LegacyVersion : descriptor.CurrentVersion;

            _logger.LogInformation(
                "Store '{Store}' at {Path} has no schema marker; treating it as {Kind} at v{Version}",
                descriptor.StoreName, storePath, isLegacy ? "existing data" : "a new store", assumed);

            await ApplyPendingAsync(descriptor, storePath, assumed, stampWhenCurrent: true, cancellationToken);
            return;
        }

        if (marker.Version > descriptor.CurrentVersion)
        {
            // Written by a newer build. Downgrading is not something we can do, and refusing to
            // start would strand a rollback, so read it as-is and say so loudly.
            _logger.LogWarning(
                "Store '{Store}' at {Path} is at schema v{Found}, ahead of this build's v{Expected}; " +
                "continuing without migrating",
                descriptor.StoreName, storePath, marker.Version, descriptor.CurrentVersion);
            return;
        }

        await ApplyPendingAsync(descriptor, storePath, marker.Version, stampWhenCurrent: false, cancellationToken);
    }

    private async Task ApplyPendingAsync(
        StoreSchemaDescriptor descriptor,
        string storePath,
        int currentVersion,
        bool stampWhenCurrent,
        CancellationToken cancellationToken)
    {
        if (currentVersion == descriptor.CurrentVersion)
        {
            if (!stampWhenCurrent)
                return;

            if (_options.DryRun)
                _logger.LogInformation(
                    "[dry run] Would stamp store '{Store}' at v{Version}",
                    descriptor.StoreName, currentVersion);
            else
                await StampAsync(descriptor, storePath, currentVersion, cancellationToken);

            return;
        }

        while (currentVersion < descriptor.CurrentVersion)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var migration = SelectMigration(descriptor, currentVersion);
            var context = new SchemaMigrationContext(
                descriptor.StoreName, storePath, currentVersion, migration.ToVersion);

            if (_options.DryRun)
            {
                _logger.LogInformation(
                    "[dry run] Would migrate store '{Store}' at {Path} from v{From} to v{To} using {Migration}",
                    descriptor.StoreName, storePath, currentVersion, migration.ToVersion,
                    migration.GetType().Name);
                currentVersion = migration.ToVersion;
                continue;
            }

            _logger.LogInformation(
                "Migrating store '{Store}' at {Path} from v{From} to v{To} using {Migration}",
                descriptor.StoreName, storePath, currentVersion, migration.ToVersion,
                migration.GetType().Name);

            Directory.CreateDirectory(storePath);
            await migration.MigrateAsync(context, cancellationToken);

            currentVersion = migration.ToVersion;

            // Stamped per step, not once at the end: a migration that fails three steps in
            // leaves the marker at the last step that actually completed, so the restart picks
            // up where it stopped instead of replaying steps that already succeeded.
            await StampAsync(descriptor, storePath, currentVersion, cancellationToken);
        }
    }

    private ISchemaMigration SelectMigration(StoreSchemaDescriptor descriptor, int fromVersion)
    {
        var candidates = _migrations
            .Where(m => string.Equals(m.StoreName, descriptor.StoreName, StringComparison.Ordinal)
                        && m.FromVersion == fromVersion)
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Store '{descriptor.StoreName}' is at schema v{fromVersion} and this build expects " +
                $"v{descriptor.CurrentVersion}, but no migration from v{fromVersion} is registered. " +
                "The store cannot be read safely; register the missing migration or roll back.");

        if (candidates.Count > 1)
            throw new InvalidOperationException(
                $"Store '{descriptor.StoreName}' has {candidates.Count} migrations registered from " +
                $"v{fromVersion} ({string.Join(", ", candidates.Select(c => c.GetType().Name))}). " +
                "Exactly one migration may claim a version step.");

        var migration = candidates[0];
        if (migration.ToVersion <= fromVersion)
            throw new InvalidOperationException(
                $"Migration {migration.GetType().Name} for store '{descriptor.StoreName}' targets " +
                $"v{migration.ToVersion} from v{fromVersion}. Migrations are forward-only.");

        return migration;
    }

    private Task StampAsync(
        StoreSchemaDescriptor descriptor,
        string storePath,
        int version,
        CancellationToken cancellationToken) =>
        StoreSchemaMarkerFile.WriteAsync(
            storePath, descriptor.StoreName, version, _time.GetUtcNow(), cancellationToken);
}
