using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Hosted service that loads the <see cref="AgentProfile"/> during startup
/// and watches profile files for changes, reloading automatically.
/// Also loads and watches the agent display name from <c>agent-name.md</c>.
/// </summary>
internal sealed class AgentProfileLoader : IHostedService, IDisposable
{
    private readonly IAgentProfileProvider _provider;
    private readonly ProfileHolder _holder;
    private readonly AgentNameHolder _nameHolder;
    private readonly AgentProfileOptions _options;
    private readonly IMessagePublisher _publisher;
    private readonly AgentIdentity _agent;
    private readonly ILogger<AgentProfileLoader> _logger;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private int _reloadPending;

    public AgentProfileLoader(
        IAgentProfileProvider provider,
        ProfileHolder holder,
        AgentNameHolder nameHolder,
        IOptions<AgentProfileOptions> options,
        IMessagePublisher publisher,
        AgentIdentity agent,
        ILogger<AgentProfileLoader> logger)
    {
        _provider = provider;
        _holder = holder;
        _nameHolder = nameHolder;
        _options = options.Value;
        _publisher = publisher;
        _agent = agent;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Loading agent profile...");
        var profile = await _provider.LoadAsync(cancellationToken);
        _holder.Update(profile);
        _logger.LogInformation("Agent profile loaded successfully");

        LoadAgentName();
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
            LoadAgentName();
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

    private void LoadAgentName()
    {
        try
        {
            var previousName = _nameHolder.DisplayName;

            var path = ResolveAgentNamePath();
            if (File.Exists(path))
            {
                var content = File.ReadAllText(path);
                var name = content
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                _nameHolder.Update(name);
                _logger.LogInformation("Agent display name loaded: {Name}", name ?? "(cleared)");
            }
            else
            {
                _nameHolder.Update(null);
                _logger.LogDebug("Agent name file not found at {Path}, using identity name", path);
            }

            var currentName = _nameHolder.DisplayName ?? _agent.Name;
            if (!string.Equals(previousName, _nameHolder.DisplayName, StringComparison.Ordinal))
                _ = PublishNameChangedAsync(currentName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load agent name, keeping previous value");
        }
    }

    private async Task PublishNameChangedAsync(string agentName)
    {
        try
        {
            var notification = new AgentNameChanged { AgentName = agentName };
            var envelope = notification.ToEnvelope<AgentNameChanged>(source: _agent.Name);
            await _publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{_agent.Name}", envelope);
            _logger.LogInformation("Published agent name change notification: {Name}", agentName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish agent name change notification");
        }
    }

    private string ResolveAgentNamePath()
    {
        var namePath = _options.AgentNamePath;
        if (Path.IsPathRooted(namePath))
            return namePath;

        var baseDir = Path.IsPathRooted(_options.BasePath)
            ? _options.BasePath
            : Path.Combine(AppContext.BaseDirectory, _options.BasePath);

        return Path.Combine(baseDir, namePath);
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
