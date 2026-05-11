using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RockBot.AdvisorCouncil.Personas;

/// <summary>
/// Watches the personas directory and triggers <see cref="PersonaRegistry.Reload"/>
/// on change with a 500 ms debounce.
/// </summary>
internal sealed class PersonaRegistryHotReload(
    PersonaRegistry registry,
    ILogger<PersonaRegistryHotReload> logger) : IHostedService, IDisposable
{
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly object _gate = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(registry.PersonasPath))
        {
            logger.LogWarning(
                "Hot-reload disabled: personas path {Path} does not exist",
                registry.PersonasPath);
            return Task.CompletedTask;
        }

        try
        {
            _watcher = new FileSystemWatcher(registry.PersonasPath, "*.md")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnChanged;

            logger.LogInformation("Persona hot-reload watching {Path}", registry.PersonasPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start persona hot-reload watcher on {Path}", registry.PersonasPath);
        }

        return Task.CompletedTask;
    }

    private void OnChanged(object? sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                try
                {
                    registry.Reload();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Persona reload failed after file change: {File}", e.FullPath);
                }
            }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}
