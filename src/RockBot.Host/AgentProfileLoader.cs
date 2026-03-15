using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Hosted service that loads the <see cref="AgentProfile"/> during startup
/// and watches profile files for changes, reloading automatically.
/// </summary>
internal sealed class AgentProfileLoader : IHostedService, IDisposable
{
    private readonly IAgentProfileProvider _provider;
    private readonly ProfileHolder _holder;
    private readonly AgentProfileOptions _options;
    private readonly ILogger<AgentProfileLoader> _logger;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private int _reloadPending;

    public AgentProfileLoader(
        IAgentProfileProvider provider,
        ProfileHolder holder,
        IOptions<AgentProfileOptions> options,
        ILogger<AgentProfileLoader> logger)
    {
        _provider = provider;
        _holder = holder;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Loading agent profile...");
        var profile = await _provider.LoadAsync(cancellationToken);
        _holder.Update(profile);
        _logger.LogInformation("Agent profile loaded successfully");

        StartWatching();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopWatching();
        return Task.CompletedTask;
    }

    private void StartWatching()
    {
        var baseDir = Path.IsPathRooted(_options.BasePath)
            ? _options.BasePath
            : Path.Combine(AppContext.BaseDirectory, _options.BasePath);

        if (!Directory.Exists(baseDir))
        {
            _logger.LogWarning("Profile directory {Path} does not exist, file watching disabled", baseDir);
            return;
        }

        _watcher = new FileSystemWatcher(baseDir, "*.md")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;

        _logger.LogInformation("Watching profile directory {Path} for changes", baseDir);
    }

    private void StopWatching()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        _debounce?.Dispose();
        _debounce = null;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher fires multiple events per save; debounce to 500ms
        if (Interlocked.Exchange(ref _reloadPending, 1) == 0)
        {
            _debounce?.Dispose();
            _debounce = new Timer(
                _ => _ = ReloadAsync(),
                null,
                TimeSpan.FromMilliseconds(500),
                Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            _logger.LogInformation("Profile file change detected, reloading...");
            var profile = await _provider.LoadAsync(CancellationToken.None);
            _holder.Update(profile);
            _logger.LogInformation("Agent profile reloaded successfully (version {Version})", _holder.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload agent profile, keeping previous version");
        }
        finally
        {
            Interlocked.Exchange(ref _reloadPending, 0);
        }
    }

    public void Dispose()
    {
        StopWatching();
    }
}

/// <summary>
/// Holds the loaded <see cref="AgentProfile"/> and tracks a version counter
/// that increments on each update. Thread-safe for concurrent readers.
/// </summary>
public sealed class ProfileHolder
{
    private volatile AgentProfile? _profile;
    private long _version;

    /// <summary>
    /// Current profile version. Increments on each <see cref="Update"/> call.
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    public AgentProfile Profile
    {
        get => _profile ?? throw new InvalidOperationException(
            "Agent profile has not been loaded yet. Ensure AgentProfileLoader has started.");
    }

    /// <summary>
    /// Atomically replaces the profile and increments the version counter.
    /// </summary>
    public void Update(AgentProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Interlocked.Increment(ref _version);
    }
}
