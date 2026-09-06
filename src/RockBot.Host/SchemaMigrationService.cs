using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Runs the schema migration check at host startup, before any store is read.
/// </summary>
/// <remarks>
/// Registered as the first <see cref="IHostedService"/> so its <see cref="StartAsync"/> runs
/// ahead of every store that has one. Note that .NET constructs the whole hosted-service set
/// before starting any of it, so other services' constructors have already run by this point —
/// which is why migrations are handed a path rather than a store service.
/// </remarks>
internal sealed class SchemaMigrationService : IHostedService
{
    private readonly SchemaMigrationRunner _runner;
    private readonly ILogger<SchemaMigrationService> _logger;

    public SchemaMigrationService(SchemaMigrationRunner runner, ILogger<SchemaMigrationService> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Deliberately not caught: a store this build cannot read is worse than a host that
        // refuses to start, because the agent would carry on writing v-next records over data
        // the migration was meant to convert.
        await _runner.RunAsync(cancellationToken);
        _logger.LogDebug("Schema migration check complete");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
