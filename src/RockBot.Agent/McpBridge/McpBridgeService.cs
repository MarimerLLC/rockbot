using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RockBot.Agent.McpBridge.ArgGuards;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Agent.McpBridge.Auth;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Auth;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.McpBridge;

/// <summary>
/// Hosted service that manages MCP server connections, handles tool invoke requests
/// from the message bus, and publishes tool discovery/response messages.
/// </summary>
public sealed class McpBridgeService : IHostedService, IAsyncDisposable
{
    private readonly IMessagePublisher _publisher;
    private readonly IMessageSubscriber _subscriber;
    private readonly McpBridgeOptions _options;
    private readonly string _agentName;
    private readonly string _configPath;
    private readonly ILogger<McpBridgeService> _logger;
    private readonly ILlmClient? _llmClient;
    private readonly ITokenProviderRegistry? _tokenProviders;
    private readonly WorkIqHealthTracker? _healthTracker;
    private readonly IMcpArgGuardRegistry? _argGuards;
    private readonly IMcpElicitationResponder? _elicitationResponder;

    /// <summary>Resolves a server's named elicitation responder (keyed services), if it names one.</summary>
    private readonly IServiceProvider? _services;

    /// <summary>
    /// Configured servers and a consistent snapshot of each connected one. Connect, refresh and
    /// disconnect for a server run one at a time under its lock; calls lease a snapshot so the
    /// client they use isn't disposed under them. See issue #604.
    /// </summary>
    private readonly McpServerConnections _connections = new();

    /// <summary>How long shutdown waits for in-flight calls before clients are dropped.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When each connected server's tool and prompt lists were last read, for the periodic
    /// surface refresh. Concurrent: written by connect, refresh and the sweep.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSurfaceCheck = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>Servers with a debounced list_changed refresh already queued.</summary>
    private readonly ConcurrentDictionary<string, byte> _pendingSurfaceRefresh = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan SurfaceRefreshDebounce = TimeSpan.FromMilliseconds(500);


    private readonly Lazy<IAttachmentStorage> _attachmentStorage;

    /// <summary>
    /// Response-side binary capture. Unlike <see cref="AttachmentGateway"/> this needs no
    /// manifest and no HTTP client, so a single instance serves every server — including the
    /// ones with no <c>attachments</c> block, which are exactly the ones it exists for.
    /// </summary>
    private readonly Lazy<BinaryResponseCapture> _binaryCapture;
    private readonly SemaphoreSlim _configPersistLock = new(1, 1);
    private ISubscription? _invokeSubscription;
    private ISubscription? _refreshSubscription;
    private ISubscription? _manageSubscription;
    private FileSystemWatcher? _configWatcher;
    private Task? _reconnectSweepTask;
    private Task? _configPollTask;
    private CancellationTokenSource? _sweepCts;
    private Timer? _reloadDebounce;
    private int _reloadPending;
    private readonly object _stampGate = new();
    private ConfigStamp? _lastConfigStamp;

    /// <summary>
    /// Set after the initial MCP connections are established in <see cref="StartAsync"/>.
    /// Refresh requests whose envelope timestamp predates this moment are stale —
    /// they were queued before the bridge started and the startup publication already
    /// covers them, so we discard them to avoid sending tool lists twice.
    /// </summary>
    private DateTimeOffset _startupCompletedAt;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public McpBridgeService(
        IMessagePublisher publisher,
        IMessageSubscriber subscriber,
        AgentIdentity identity,
        IOptions<McpBridgeOptions> options,
        ILogger<McpBridgeService> logger,
        ILlmClient? llmClient = null,
        ITokenProviderRegistry? tokenProviders = null,
        WorkIqHealthTracker? healthTracker = null,
        IMcpArgGuardRegistry? argGuards = null,
        IMcpElicitationResponder? elicitationResponder = null,
        IServiceProvider? services = null)
        : this(publisher, subscriber, identity, options, logger, llmClient, tokenProviders, healthTracker,
            argGuards, elicitationResponder, services, attachmentStorage: null)
    {
    }

    /// <summary>
    /// Also takes the attachment storage, which otherwise defaults to the shared-volume path from
    /// <c>ROCKBOT_SHARED_PATH</c>. Internal so DI keeps resolving the public constructor and the
    /// bridge keeps its own instance in production; tests point it at a temporary directory.
    /// </summary>
    internal McpBridgeService(
        IMessagePublisher publisher,
        IMessageSubscriber subscriber,
        AgentIdentity identity,
        IOptions<McpBridgeOptions> options,
        ILogger<McpBridgeService> logger,
        ILlmClient? llmClient,
        ITokenProviderRegistry? tokenProviders,
        WorkIqHealthTracker? healthTracker,
        IMcpArgGuardRegistry? argGuards,
        IMcpElicitationResponder? elicitationResponder,
        IServiceProvider? services,
        IAttachmentStorage? attachmentStorage)
    {
        _attachmentStorage = attachmentStorage is not null
            ? new Lazy<IAttachmentStorage>(attachmentStorage)
            : new Lazy<IAttachmentStorage>(() => new AttachmentStorage());
        _publisher = publisher;
        _subscriber = subscriber;
        _options = options.Value;
        _agentName = identity.Name;
        _configPath = Path.IsPathRooted(_options.ConfigPath)
            ? _options.ConfigPath
            : Path.Combine(AppContext.BaseDirectory, _options.ConfigPath);
        _logger = logger;
        _llmClient = llmClient;
        _tokenProviders = tokenProviders;
        _healthTracker = healthTracker;
        _argGuards = argGuards;
        _elicitationResponder = elicitationResponder;
        _services = services;
        _binaryCapture = new Lazy<BinaryResponseCapture>(
            () => new BinaryResponseCapture(_attachmentStorage.Value, _logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe to tool invoke requests
        _invokeSubscription = await _subscriber.SubscribeAsync(
            McpToolProxy.InvokeTopic,
            $"mcp-bridge.{_agentName}",
            HandleToolInvokeAsync,
            cancellationToken);

        // Subscribe to metadata refresh requests
        _refreshSubscription = await _subscriber.SubscribeAsync(
            "tool.meta.mcp.refresh",
            $"mcp-bridge.{_agentName}.refresh",
            HandleRefreshRequestAsync,
            cancellationToken);

        // Subscribe to management requests (get-details, register, unregister)
        _manageSubscription = await _subscriber.SubscribeAsync(
            McpManagementExecutor.ManageTopic,
            $"mcp-bridge.{_agentName}.manage",
            HandleManagementRequestAsync,
            cancellationToken);

        // Wire health-tracker changes so workiq-* tools appear/disappear
        // from the published tool list as the auth cache becomes valid/invalid.
        if (_healthTracker is not null)
        {
            _healthTracker.HealthChanged += OnAuthHealthChanged;
        }

        // Load config and connect to servers
        await LoadConfigAndConnectAsync(cancellationToken);
        _startupCompletedAt = DateTimeOffset.UtcNow;

        // Watch for config changes
        SetupConfigWatcher();

        // Start periodic reconnect sweep for any servers that failed to connect,
        // and the config-poll fallback (catches edits the FileSystemWatcher misses).
        if (_options.ReconnectSweepIntervalSeconds > 0 || _options.ConfigPollIntervalSeconds > 0)
        {
            _sweepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (_options.ReconnectSweepIntervalSeconds > 0)
                _reconnectSweepTask = RunReconnectSweepAsync(_sweepCts.Token);

            if (_options.ConfigPollIntervalSeconds > 0)
                _configPollTask = RunConfigPollAsync(_sweepCts.Token);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _configWatcher?.Dispose();
        _configWatcher = null;

        _reloadDebounce?.Dispose();
        _reloadDebounce = null;

        if (_healthTracker is not null)
            _healthTracker.HealthChanged -= OnAuthHealthChanged;

        if (_sweepCts is not null)
        {
            await _sweepCts.CancelAsync();
            if (_reconnectSweepTask is not null)
                await _reconnectSweepTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (_configPollTask is not null)
                await _configPollTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _sweepCts.Dispose();
        }

        if (_invokeSubscription is not null)
            await _invokeSubscription.DisposeAsync();
        if (_refreshSubscription is not null)
            await _refreshSubscription.DisposeAsync();
        if (_manageSubscription is not null)
            await _manageSubscription.DisposeAsync();

        await DisposeClientsAsync();
    }

    private async Task LoadConfigAndConnectAsync(CancellationToken ct)
    {
        McpBridgeConfig config;

        // Capture the on-disk stamp *before* reading so that an edit landing during the
        // (potentially multi-second) connect loop below is still seen by the next poll:
        // we record the stamp of what we actually loaded, not a re-read at the end. If we
        // self-write (seed/dedup) the stamp is refreshed afterward instead. (issue #470)
        var stampAtLoad = ReadConfigStamp(_configPath);

        if (!File.Exists(_configPath))
        {
            _logger.LogInformation("MCP config file not found at {Path}; starting with empty config", _configPath);
            config = new McpBridgeConfig();
        }
        else
        {
            try
            {
                var json = await File.ReadAllTextAsync(_configPath, ct);
                config = JsonSerializer.Deserialize<McpBridgeConfig>(json, JsonOptions)
                    ?? new McpBridgeConfig();

                // If the primary file deserialized to empty but had content, try the backup
                if (config.McpServers.Count == 0 && json.Trim().Length > 0)
                {
                    _logger.LogWarning(
                        "MCP config at {Path} deserialized to empty McpServers but file was non-empty ({Length} chars) — attempting backup",
                        _configPath, json.Length);
                    config = await TryLoadFromBackupAsync(ct) ?? config;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read MCP config from {Path}", _configPath);

                // Attempt to recover from backup
                var backupConfig = await TryLoadFromBackupAsync(ct);
                if (backupConfig is not null)
                {
                    config = backupConfig;
                }
                else
                {
                    return;
                }
            }
        }

        // Remove duplicate entries that point at the same URL with the same credentials/options.
        // Multiple entries can accumulate when the helm auto-seed adds a default name while the
        // user has already registered the same server manually under a different name.
        var removedDupes = DeduplicateByIdentity(config);

        // Seed default servers from infrastructure config (Helm values). Skip seeding when an
        // entry with the same URL already exists under any name — the user's existing entry
        // (which may carry auth headers) takes precedence.
        var seeded = false;
        foreach (var (name, url) in _options.DefaultServers)
        {
            var normalizedDefaultUrl = McpBridgeServerConfig.NormalizeUrl(url);
            var matchByUrl = string.IsNullOrEmpty(normalizedDefaultUrl)
                ? default
                : config.McpServers.FirstOrDefault(kvp =>
                    McpBridgeServerConfig.NormalizeUrl(kvp.Value.Url) == normalizedDefaultUrl);
            if (matchByUrl.Key is not null)
            {
                if (!string.Equals(matchByUrl.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Default MCP server {Name} ({Url}) already exists as {ExistingName}; skipping seed",
                        name, url, matchByUrl.Key);
                }
                continue;
            }

            _logger.LogInformation("Seeding default MCP server {Name} at {Url}", name, url);
            config.McpServers[name] = new McpBridgeServerConfig
            {
                Type = "sse",
                Url = url
            };
            seeded = true;
        }

        // Every entry gets a stable id once, persisted below. Existing names that break the
        // rules for new registrations still load — renaming them would break whatever already
        // refers to them — but the operator is told.
        var backfilledIds = 0;
        foreach (var (name, entry) in config.McpServers)
        {
            if (string.IsNullOrEmpty(entry.Id))
            {
                entry.Id = McpServerNames.NewId();
                backfilledIds++;
            }

            if (McpServerNames.Validate(name) is { } nameProblem)
                _logger.LogWarning("MCP server entry in {Path}: {Problem}", _configPath, nameProblem);
        }

        if (seeded || removedDupes > 0 || backfilledIds > 0)
        {
            try
            {
                var updatedJson = JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });
                await File.WriteAllTextAsync(_configPath, updatedJson, ct);
                _logger.LogInformation(
                    "Persisted MCP config changes to {Path} (seeded={Seeded}, duplicatesRemoved={Removed}, idsAssigned={Ids})",
                    _configPath, seeded, removedDupes, backfilledIds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist seeded MCP servers to {Path}", _configPath);
            }
        }

        // Disconnect servers that are no longer in config
        var removedServers = _connections.ConfiguredNames.Except(config.McpServers.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var name in removedServers)
        {
            await DisconnectServerAsync(name);
        }

        // Connect to new/updated servers
        foreach (var (name, serverConfig) in config.McpServers)
        {
            await ConnectServerAsync(name, serverConfig, ct);
        }

        // Mark this config as seen. If we self-wrote above (seeding/dedup) the on-disk
        // file is newer than what we read, so re-read its stamp to avoid a redundant
        // reload; otherwise record the pre-read stamp so any edit that landed mid-load is
        // still detected by the next poll.
        var didSelfWrite = seeded || removedDupes > 0 || backfilledIds > 0;
        var stampToRemember = didSelfWrite ? ReadConfigStamp(_configPath) : stampAtLoad;
        lock (_stampGate)
        {
            _lastConfigStamp = stampToRemember;
        }
    }

    /// <summary>
    /// Attempts to load MCP config from the backup file. Returns null if no backup exists
    /// or if the backup also fails to deserialize.
    /// </summary>
    private async Task<McpBridgeConfig?> TryLoadFromBackupAsync(CancellationToken ct)
    {
        var backupPath = _configPath + ".bak";
        if (!File.Exists(backupPath))
        {
            _logger.LogWarning("No backup config file found at {BackupPath}", backupPath);
            return null;
        }

        try
        {
            var backupJson = await File.ReadAllTextAsync(backupPath, ct);
            var backupConfig = JsonSerializer.Deserialize<McpBridgeConfig>(backupJson, JsonOptions);

            if (backupConfig?.McpServers.Count > 0)
            {
                _logger.LogInformation(
                    "Recovered MCP config from backup {BackupPath} with servers: [{Servers}]",
                    backupPath, string.Join(", ", backupConfig.McpServers.Keys));
                return backupConfig;
            }

            _logger.LogWarning("Backup config at {BackupPath} also has empty McpServers", backupPath);
            return null;
        }
        catch (Exception backupEx)
        {
            _logger.LogError(backupEx, "Failed to read backup MCP config from {BackupPath}", backupPath);
            return null;
        }
    }

    /// <summary>
    /// Removes entries from <paramref name="config"/> that share a canonical identity
    /// (same URL, credentials, transport, and options) with another entry. When duplicates
    /// are found, prefers keeping the entry whose name matches a helm-default server name
    /// so a subsequent seed does not re-add what we just removed; otherwise keeps the
    /// alphabetically first name. Returns the number of entries removed.
    /// </summary>
    private int DeduplicateByIdentity(McpBridgeConfig config)
    {
        var groups = config.McpServers
            .GroupBy(kvp => kvp.Value.CanonicalIdentity(), StringComparer.Ordinal)
            .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1)
            .ToList();

        var removed = 0;
        foreach (var group in groups)
        {
            var entries = group.ToList();
            var preferred = entries.FirstOrDefault(e =>
                _options.DefaultServers.ContainsKey(e.Key));
            // Never drop an operator's entry in favour of one the agent registered.
            if (preferred.Key is null)
                preferred = entries.FirstOrDefault(e => !e.Value.IsAgentOwned());
            if (preferred.Key is null)
            {
                preferred = entries
                    .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                    .First();
            }

            foreach (var entry in entries)
            {
                if (string.Equals(entry.Key, preferred.Key, StringComparison.Ordinal))
                    continue;

                config.McpServers.Remove(entry.Key);
                removed++;
                _logger.LogWarning(
                    "Removed duplicate MCP server entry {DuplicateName} (same URL/credentials as kept entry {KeptName}: {Url})",
                    entry.Key, preferred.Key, entry.Value.Url);
            }
        }
        return removed;
    }

    /// <summary>
    /// Connects (or reconnects) <paramref name="name"/> with <paramref name="config"/> and publishes
    /// its snapshot. Runs under the server's lock, so the reconnect sweep, a config reload and an
    /// invoke's reconnect-and-retry queue behind one another instead of racing to build clients.
    /// A caller that passes <paramref name="replacing"/> wants a reconnect only while that client
    /// is still current, and one that passes <paramref name="onlyIfDisconnected"/> only while the
    /// server has no connection: if another caller connected it while this one waited, there is
    /// nothing to do.
    /// </summary>
    private async Task ConnectServerAsync(
        string name, McpBridgeServerConfig config, CancellationToken ct,
        McpClient? replacing = null, bool onlyIfDisconnected = false)
    {
        if (!config.IsSse)
        {
            _logger.LogWarning(
                "MCP server {Name} uses stdio transport which is not supported in embedded mode; skipping",
                name);
            return;
        }

        if (string.IsNullOrEmpty(config.Url))
        {
            _logger.LogError("SSE server {Name} missing URL", name);
            return;
        }

        // Fail closed on invalid argGuards: connecting without the declared policy would
        // silently weaken it. The server is never configured, so tool invokes get
        // server-not-found until the config is fixed. Outside the retry loop below —
        // a config error is not transient.
        var guardConfigError = McpArgGuardEvaluator.ValidateConfig(_argGuards, name, config);
        if (guardConfigError is not null)
        {
            _logger.LogError(
                "MCP server {Name} has invalid argGuards configuration — refusing to connect (fail closed): {Error}",
                name, guardConfigError);
            return;
        }

        using var serverLock = await _connections.LockAsync(name, ct);

        _connections.TryGet(name, out var previous);
        if ((replacing is not null && previous is not null && !ReferenceEquals(previous.Client, replacing))
            || (onlyIfDisconnected && previous is not null))
        {
            _logger.LogDebug("MCP server {Name} was reconnected by another caller while this one waited; skipping", name);
            return;
        }

        // Record the config before attempting the connection so the reconnect sweep can retry a
        // server that never connected.
        _connections.SetConfig(name, config);

        var maxAttempts = 1 + Math.Max(0, _options.ConnectRetryCount);
        var delayMs = _options.ConnectRetryBaseDelayMs;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            McpClient? newClient = null;
            var published = false;
            try
            {
                var httpTransportMode = config.TransportMode?.ToLowerInvariant() switch
                {
                    "sse" => ModelContextProtocol.Client.HttpTransportMode.Sse,
                    "streamable-http" or "streamable" or "http" => ModelContextProtocol.Client.HttpTransportMode.StreamableHttp,
                    _ => ModelContextProtocol.Client.HttpTransportMode.AutoDetect
                };

                var transportOptions = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(config.Url),
                    TransportMode = httpTransportMode
                };

                HttpClientTransport transport;
                var customClient = TryBuildHttpClient(name, config);
                if (customClient is not null)
                {
                    transport = new HttpClientTransport(transportOptions, customClient, loggerFactory: null, ownsHttpClient: true);
                }
                else
                {
                    transport = new HttpClientTransport(transportOptions);
                }

                // The SDK advertises the elicitation capability during initialize exactly when an
                // elicitation handler is present, so a server configured "off" is never invited to
                // ask in the first place — a cleaner answer than advertising and refusing.
                var elicitationConfig = config.Elicitation ?? _options.DefaultElicitation;
                var elicitation = McpElicitationCoordinator.TryCreate(
                    name,
                    elicitationConfig,
                    McpElicitationResponders.Resolve(
                        elicitationConfig, _elicitationResponder, _services, name, _logger,
                        isServerPolicy: config.Elicitation is not null),
                    _logger);

                var clientOptions = new McpClientOptions
                {
                    Handlers = new McpClientHandlers
                    {
                        ElicitationHandler = elicitation is null
                            ? null
                            : (elicitRequest, elicitCt) => elicitation.HandleAsync(elicitRequest, elicitCt),
                        NotificationHandlers = SurfaceChangeHandlers(name)
                    }
                };

                newClient = await McpClient.CreateAsync(transport, clientOptions, cancellationToken: ct);

                // Discover tools before publishing, so a failure leaves the current snapshot serving.
                var tools = await newClient.ListToolsAsync(cancellationToken: ct);
                var filteredTools = ApplyToolFilters(tools.ToList(), config);

                var (prompts, promptsKnown) = await ListPromptsAsync(name, newClient, previous, ct);
                var (fingerprint, toolFingerprints) = ComputeFingerprints(filteredTools, prompts, promptsKnown);

                var serverInfo = newClient.ServerInfo;
                var metadata = new McpServerMetadata(
                    ImplementationName: serverInfo?.Name,
                    Title: serverInfo?.Title,
                    Version: serverInfo?.Version,
                    Description: serverInfo?.Description,
                    Instructions: newClient.ServerInstructions);

                _logger.LogInformation(
                    "Connected to MCP server {Name} (impl={ImplName} v{Version}) with {ToolCount} tools and {PromptCount} prompts",
                    name, metadata.ImplementationName ?? "(unknown)", metadata.Version ?? "(unknown)",
                    filteredTools.Count, prompts.Count);

                // Cached so a future health flip can re-publish without re-running the (LLM-driven)
                // summary generation. Every reconnect and config reload comes through here; when the
                // server's surface and identity are unchanged the previous summary still describes
                // it, so the LLM isn't asked again.
                var summary = previous is { Summary.Fingerprint: { } previousFingerprint }
                              && previousFingerprint == fingerprint
                              && previous.Metadata == metadata
                    ? previous.Summary
                    : await GenerateSummaryAsync(name, metadata, filteredTools, prompts, ct);
                summary = summary with
                {
                    ServerId = config.Id,
                    Fingerprint = fingerprint,
                    ToolFingerprints = toolFingerprints
                };

                var resources = new ConnectionResources(newClient);
                _connections.Publish(new ConnectedServer
                {
                    Name = name,
                    Config = config,
                    Client = newClient,
                    Tools = filteredTools,
                    Prompts = prompts,
                    Metadata = metadata,
                    Summary = summary,
                    Elicitation = elicitation,
                    AttachmentGateway = CreateAttachmentGateway(name, config, resources),
                    Resources = resources
                });
                published = true;
                _lastSurfaceCheck[name] = DateTimeOffset.UtcNow;

                if (IsServerHiddenByAuth(config))
                {
                    _logger.LogInformation(
                        "MCP server {Name} uses auth profile '{Profile}' which is currently unhealthy; suppressing publish until auth recovers",
                        name, config.Auth?.Profile);
                    // Make sure the agent's prior view of this server (if any) is cleared.
                    await PublishServersIndexedAsync([], [name], ct);
                }
                else
                {
                    await PublishServersIndexedAsync([summary], [], ct);
                }
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (!published && newClient is not null)
                    await DisposeQuietlyAsync(newClient);
                throw;
            }
            catch (Exception ex)
            {
                // A client that connected but failed discovery was never published; nothing else
                // holds it.
                if (!published && newClient is not null)
                    await DisposeQuietlyAsync(newClient);

                if (attempt < maxAttempts)
                {
                    _logger.LogWarning(ex,
                        "Failed to connect to MCP server {Name} (attempt {Attempt}/{Max}), retrying in {Delay}ms",
                        name, attempt, maxAttempts, delayMs);
                    await Task.Delay(delayMs, ct);
                    delayMs *= 2;
                }
                else
                {
                    _logger.LogError(ex,
                        "Failed to connect to MCP server {Name} after {Max} attempt(s)",
                        name, maxAttempts);
                }
            }
        }

        // Every attempt failed, so the previous connection (if any) keeps serving. If the config
        // changed, apply its policy to that connection now rather than at the next successful
        // connect: re-filtering the previous tool list can only narrow it, so an operator who
        // just denied a tool isn't overruled by a server that happened to be down.
        if (previous is not null && !ReferenceEquals(previous.Config, config))
        {
            _connections.Publish(previous with
            {
                Config = config,
                Tools = ApplyToolFilters([.. previous.Tools], config)
            });
        }
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try { await disposable.DisposeAsync(); }
        catch { /* Best-effort cleanup */ }
    }

    private async Task DisconnectServerAsync(string name)
    {
        using (await _connections.LockAsync(name, CancellationToken.None))
        {
            _connections.Remove(name, forgetConfig: true);
            _lastSurfaceCheck.TryRemove(name, out _);
        }

        await PublishServersIndexedAsync([], [name], CancellationToken.None);

        _logger.LogInformation("Disconnected from MCP server {Name}", name);
    }

    /// <summary>
    /// The connection's attachment gateway, built on first use. Its HTTP client carries the same
    /// auth and headers as MCP tool calls and is disposed with the connection.
    /// </summary>
    private Lazy<AttachmentGateway?> CreateAttachmentGateway(
        string serverName, McpBridgeServerConfig config, ConnectionResources resources) => new(() =>
    {
        if (config.Attachments is null || string.IsNullOrEmpty(config.Url))
            return null;

        var http = TryBuildHttpClient(serverName, config) ?? new HttpClient();
        resources.Add(http);
        return new AttachmentGateway(_attachmentStorage.Value, http, new Uri(config.Url), config.Attachments, _logger);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Builds an <see cref="HttpClient"/> for a server config that needs custom
    /// headers, bearer auth, or both. Returns <c>null</c> when neither applies,
    /// signalling that the caller can use the transport's default client.
    /// </summary>
    private HttpClient? TryBuildHttpClient(string serverName, McpBridgeServerConfig config)
    {
        var hasHeaders = config.Headers.Count > 0;
        var hasAuth = config.Auth is not null;
        if (!hasHeaders && !hasAuth) return null;

        HttpClient httpClient;
        if (hasAuth)
        {
            if (_tokenProviders is null)
            {
                throw new InvalidOperationException(
                    $"MCP server '{serverName}' requires auth profile '{config.Auth!.Profile}' " +
                    $"but no ITokenProviderRegistry is registered in DI. " +
                    $"Add a token provider (e.g. services.AddWorkIqAuth(...)) before connecting.");
            }

            var provider = _tokenProviders.Get(config.Auth!.Profile);
            var bearerHandler = new BearerInjectionHandler(provider, new SocketsHttpHandler());
            httpClient = new HttpClient(bearerHandler);
        }
        else
        {
            httpClient = new HttpClient();
        }

        foreach (var (key, rawValue) in config.Headers)
        {
            // Never let static headers clobber the auth handler's bearer.
            if (hasAuth && string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "MCP server '{Server}' has both static 'Authorization' header and auth profile '{Profile}'; ignoring the static header in favor of the bearer-injecting auth handler",
                    serverName, config.Auth!.Profile);
                continue;
            }

            var expanded = ExpandEnvVars(rawValue);
            if (!string.IsNullOrEmpty(expanded))
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, expanded);
        }

        return httpClient;
    }

    /// <summary>
    /// Runs response-side binary capture for any server. Capture applies whether or not the
    /// server has an attachment manifest — a manifest only supplies the declarative field rules
    /// and the switch to turn capture off.
    /// </summary>
    private Task<CallToolResult> CaptureBinaryContentAsync(
        string serverName,
        McpBridgeServerConfig? config,
        string toolName,
        CallToolResult result,
        CancellationToken ct)
    {
        return _binaryCapture.Value.CaptureAsync(
            serverName, toolName, result, config?.Attachments?.Capture, ct);
    }


    private static List<McpClientTool> ApplyToolFilters(List<McpClientTool> tools, McpBridgeServerConfig config)
    {
        if (config.AllowedTools.Count > 0)
        {
            var allowed = new HashSet<string>(config.AllowedTools, StringComparer.OrdinalIgnoreCase);
            return tools.Where(t => allowed.Contains(t.Name)).ToList();
        }

        if (config.DeniedTools.Count > 0)
        {
            var denied = new HashSet<string>(config.DeniedTools, StringComparer.OrdinalIgnoreCase);
            return tools.Where(t => !denied.Contains(t.Name)).ToList();
        }

        return tools;
    }

    /// <summary>
    /// Lists a server's prompts. <c>Known</c> is false only when the list couldn't be read: a
    /// server without the prompts capability, or one answering method-not-found, simply has none.
    /// On a failed read the prompts <paramref name="previous"/> knew are kept, so a transient error
    /// doesn't make them vanish, and the surface fingerprint becomes unknown rather than recording a
    /// surface that was never seen (mcp-aggregator#41 baselined a failed read and read stale forever).
    /// </summary>
    private async Task<(List<McpClientPrompt> Prompts, bool Known)> ListPromptsAsync(
        string name, McpClient client, ConnectedServer? previous, CancellationToken ct)
    {
        if (client.ServerCapabilities?.Prompts is null)
            return ([], true);

        try
        {
            return ([.. await client.ListPromptsAsync(cancellationToken: ct)], true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (McpProtocolException ex) when (ex.ErrorCode == McpErrorCode.MethodNotFound)
        {
            _logger.LogDebug("MCP server {Name} advertises prompts but does not implement prompts/list", name);
            return ([], true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing prompts on MCP server {Name} failed; its surface fingerprint is unknown", name);
            return ([.. previous?.Prompts ?? []], false);
        }
    }

    private static McpPromptDefinition ToPromptDefinition(McpClientPrompt prompt) => new()
    {
        Name = prompt.Name,
        Description = prompt.Description,
        Arguments = (prompt.ProtocolPrompt.Arguments ?? [])
            .Select(a => new McpPromptArgument { Name = a.Name, Description = a.Description, Required = a.Required ?? false })
            .ToList()
    };

    private static string? RawSchema(McpClientTool tool) =>
        tool.JsonSchema.ValueKind != JsonValueKind.Undefined ? tool.JsonSchema.GetRawText() : null;

    /// <summary>
    /// The server fingerprint (null when the prompt list couldn't be read) and per-tool
    /// fingerprints for a surface.
    /// </summary>
    private static (string? Server, Dictionary<string, string> Tools) ComputeFingerprints(
        IReadOnlyList<McpClientTool> tools, IReadOnlyList<McpClientPrompt> prompts, bool promptsKnown)
    {
        var toolFingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools)
            toolFingerprints.TryAdd(tool.Name, McpSurfaceFingerprint.Tool(tool.Name, tool.Description, RawSchema(tool)));

        var server = promptsKnown
            ? McpSurfaceFingerprint.Server(
                tools.Select(t => (t.Name, t.Description, RawSchema(t))),
                prompts.Select(ToPromptDefinition))
            : null;

        return (server, toolFingerprints);
    }

    /// <summary>
    /// Client notification handlers that queue a surface refresh when the server says its tool or
    /// prompt list changed. Servers that never send these are covered by the periodic refresh.
    /// </summary>
    private IEnumerable<KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>> SurfaceChangeHandlers(string name)
    {
        Func<JsonRpcNotification, CancellationToken, ValueTask> handler = (_, _) =>
        {
            QueueSurfaceRefresh(name);
            return ValueTask.CompletedTask;
        };

        return
        [
            new(NotificationMethods.ToolListChangedNotification, handler),
            new(NotificationMethods.PromptListChangedNotification, handler),
        ];
    }

    /// <summary>
    /// Debounces list_changed bursts into one refresh per server. A notification that arrives
    /// while a refresh is running queues another, so the last change is never missed.
    /// </summary>
    private void QueueSurfaceRefresh(string name)
    {
        if (!_pendingSurfaceRefresh.TryAdd(name, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SurfaceRefreshDebounce);
                _pendingSurfaceRefresh.TryRemove(name, out _);
                await RefreshSurfaceAsync(name, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Surface refresh for MCP server {Name} failed", name);
            }
            finally
            {
                _pendingSurfaceRefresh.TryRemove(name, out _);
            }
        });
    }

    /// <summary>
    /// Reconnects a configured server with its current configuration, as the reconnect sweep and
    /// a config reload do. For tests: there is no model-facing way to force a reconnect.
    /// </summary>
    internal async Task<bool> ReconnectAsync(string name, CancellationToken ct)
    {
        if (!_connections.TryGetConfig(name, out var config))
            return false;
        await ConnectServerAsync(name, config, ct);
        return _connections.TryGet(name, out _);
    }

    /// <summary>Why the agent can't change <paramref name="name"/> through <paramref name="tool"/>.</summary>
    private static string OperatorManaged(string name, string tool) =>
        $"The MCP server '{name}' is managed by the operator, so {tool} can't change it. " +
        "Its configuration can only be changed in the agent's MCP configuration; ask the user if it needs to change.";

    /// <summary>
    /// Re-reads a connected server's tools and prompts on its existing connection and, when the
    /// surface fingerprint moved, publishes a new snapshot of that connection, regenerates the
    /// summary and announces the change. An unchanged surface publishes nothing. Returns true when
    /// a change was published. A server whose lists can't be read is left alone: a dead connection
    /// is the reconnect path's job, not this one's. Shares the server's lock with connect and
    /// disconnect, so a refresh never interleaves with a reconnect.
    /// </summary>
    internal async Task<bool> RefreshSurfaceAsync(string name, CancellationToken ct)
    {
        using var serverLock = await _connections.LockAsync(name, ct);

        using var lease = _connections.Lease(name);
        if (lease is null)
            return false;
        var current = lease.Server;

        List<McpClientTool> tools;
        try
        {
            tools = ApplyToolFilters([.. await current.Client.ListToolsAsync(cancellationToken: ct)], current.Config);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Surface refresh: listing tools on MCP server {Name} failed", name);
            return false;
        }

        var (prompts, promptsKnown) = await ListPromptsAsync(name, current.Client, current, ct);
        var (fingerprint, toolFingerprints) = ComputeFingerprints(tools, prompts, promptsKnown);
        _lastSurfaceCheck[name] = DateTimeOffset.UtcNow;

        var unchanged = fingerprint is not null
            ? current.Summary.Fingerprint == fingerprint
            : SameToolFingerprints(current.Summary.ToolFingerprints, toolFingerprints);
        if (unchanged)
            return false;

        var summary = (await GenerateSummaryAsync(name, current.Metadata, tools, prompts, ct)) with
        {
            ServerId = current.Config.Id,
            Fingerprint = fingerprint,
            ToolFingerprints = toolFingerprints
        };

        // Same connection, new surface: the snapshot keeps its resources, so nothing is retired.
        _connections.Publish(current with { Tools = tools, Prompts = prompts, Summary = summary });

        _logger.LogInformation(
            "MCP server {Name} changed its surface: now {ToolCount} tools and {PromptCount} prompts",
            name, tools.Count, prompts.Count);

        if (!IsServerHiddenByAuth(current.Config))
            await PublishServersIndexedAsync([summary], [], ct);
        return true;

        static bool SameToolFingerprints(Dictionary<string, string> a, Dictionary<string, string> b) =>
            a.Count == b.Count && a.All(kvp => b.TryGetValue(kvp.Key, out var v) && v == kvp.Value);
    }

    /// <summary>
    /// Finds <paramref name="toolName"/> among the tools <paramref name="server"/> may be called
    /// with. A miss against the snapshot's list re-lists once, without touching the snapshot, so a
    /// tool added since the last connect still goes through. Returns the tool when found;
    /// otherwise <c>Available</c> holds the names the model may use instead.
    /// </summary>
    private async Task<(McpClientTool? Tool, IReadOnlyList<string>? Available)> ResolveToolAsync(
        ConnectedServer server, string toolName, CancellationToken ct)
    {
        if (FindTool(server.Tools, toolName) is { } hit)
            return (hit, null);

        try
        {
            var fresh = ApplyToolFilters([.. await server.Client.ListToolsAsync(cancellationToken: ct)], server.Config);
            if (FindTool(fresh, toolName) is { } freshHit)
            {
                _logger.LogInformation(
                    "MCP {Server}/{Tool} is not in the cached tool list but the server lists it now; proceeding",
                    server.Name, toolName);
                return (freshHit, null);
            }

            return (null, fresh.Select(t => t.Name).ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed: letting the call through here would let a filtered-out tool past the
            // operator's deniedTools whenever tools/list happens to fail.
            _logger.LogDebug(ex, "Re-listing tools for {Server} failed; answering from the cached list", server.Name);
            return (null, server.Tools.Select(t => t.Name).ToList());
        }

        // MCP tool names are case-sensitive, but a case slip is better answered by the downstream
        // (and the unknown-tool hint) than refused here as if the tool didn't exist.
        static McpClientTool? FindTool(IReadOnlyList<McpClientTool> tools, string name) =>
            tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal))
            ?? tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Pre-checks a prompt request against the server's listed prompts: an unknown name, or
    /// missing required arguments, is answered here with the real names and signature instead of
    /// a round trip that ends in the downstream's bare protocol error. Returns null when the call
    /// may proceed.
    /// </summary>
    private static string? CheckPromptCall(ConnectedServer server, McpGetPromptRequest req)
    {
        var prompt = server.Prompts.FirstOrDefault(p => string.Equals(p.Name, req.PromptName, StringComparison.Ordinal))
                     ?? server.Prompts.FirstOrDefault(p => string.Equals(p.Name, req.PromptName, StringComparison.OrdinalIgnoreCase));
        if (prompt is null)
        {
            return McpCallDiagnostics.DescribeUnknownPrompt(
                req.ServerName, req.PromptName, server.Prompts.Select(p => p.Name).ToList());
        }

        return McpCallDiagnostics.DescribeMissingPromptArguments(
            req.ServerName, prompt.Name, ToPromptDefinition(prompt).Arguments, req.Arguments.Keys);
    }

    private static McpGetPromptResponse ToPromptResponse(McpGetPromptRequest req, GetPromptResult result) => new()
    {
        ServerName = req.ServerName,
        PromptName = req.PromptName,
        Description = result.Description,
        Messages = (result.Messages ?? []).Select(m => m.Content is TextContentBlock textBlock
            ? new McpPromptMessage { Role = m.Role.ToString().ToLowerInvariant(), Content = textBlock.Text, ContentType = "text" }
            : new McpPromptMessage
            {
                Role = m.Role.ToString().ToLowerInvariant(),
                Content = JsonSerializer.Serialize(m.Content, JsonOptions),
                ContentType = m.Content?.Type ?? "unknown"
            }).ToList()
    };
    /// <summary>
    /// Appends <see cref="McpCallDiagnostics.DescribeArgumentProblem"/>'s hint, when there is one,
    /// to a failed call's content as its own text block.
    /// </summary>
    private static (IReadOnlyList<ToolContentBlock>? Blocks, string? Content) AppendArgumentHint(
        string serverName,
        string toolName,
        string? inputSchema,
        IReadOnlyDictionary<string, object?> sentArguments,
        IReadOnlyList<ToolContentBlock>? blocks,
        string? content)
    {
        var hint = McpCallDiagnostics.DescribeArgumentProblem(serverName, toolName, inputSchema, sentArguments, content);
        if (hint is null) return (blocks, content);

        var withHint = McpElicitationNote.AppendTo(blocks, "\n" + hint);
        return (withHint, McpToolExecutor.TextFromBlocks(withHint));
    }

    private async Task<McpServerSummary> GenerateSummaryAsync(
        string serverName,
        McpServerMetadata metadata,
        IReadOnlyList<McpClientTool> tools,
        IReadOnlyList<McpClientPrompt> prompts,
        CancellationToken ct)
    {
        var toolNames = tools.Select(t => t.Name).ToList();
        var promptNames = prompts.Select(p => p.Name).ToList();

        string? summaryText = null;

        if (_options.GenerateLlmSummaries && _llmClient is not null && tools.Count > 0)
        {
            try
            {
                var toolList = string.Join("\n", tools.Take(20).Select(t =>
                    $"- {t.Name}: {t.Description}"));

                var promptSection = prompts.Count > 0
                    ? "\nPrompts:\n" + string.Join("\n", prompts.Take(10).Select(p =>
                        $"- {p.Name}: {p.Description}"))
                    : string.Empty;

                var identityLines = new List<string>();
                if (!string.IsNullOrWhiteSpace(metadata.ImplementationName))
                    identityLines.Add($"- Implementation name: {metadata.ImplementationName}");
                if (!string.IsNullOrWhiteSpace(metadata.Title))
                    identityLines.Add($"- Title: {metadata.Title}");
                if (!string.IsNullOrWhiteSpace(metadata.Version))
                    identityLines.Add($"- Version: {metadata.Version}");
                if (!string.IsNullOrWhiteSpace(metadata.Description))
                    identityLines.Add($"- Description: {metadata.Description}");

                var identitySection = identityLines.Count > 0
                    ? "Server identity (self-reported by the MCP server during initialize):\n"
                      + string.Join("\n", identityLines) + "\n\n"
                    : string.Empty;

                var instructionsSection = !string.IsNullOrWhiteSpace(metadata.Instructions)
                    ? $"Server instructions (the MCP server's own description of its purpose and usage):\n{metadata.Instructions}\n\n"
                    : string.Empty;

                var prompt = $"""
                    You are summarizing an MCP server's capabilities for an AI agent that must decide
                    which server to query for a given task. The agent sees ONLY this summary when
                    deciding — it does not see individual tool names until it calls mcp_get_service_details.

                    Write 2-4 sentences (40-80 words) for the '{serverName}' MCP server that:
                    1. State what domain it covers (e.g. email, calendar, file storage, etc.)
                    2. List the CATEGORIES of operations available (e.g. "search, read, send, and
                       organize emails; create, update, and delete calendar events; look up contacts")
                    3. Mention any notable specifics (e.g. multi-account support, specific platforms)

                    Treat the server's self-reported identity and instructions as authoritative about
                    its purpose; use the tool list to confirm and enumerate capability categories.

                    The summary must give enough detail that an agent can confidently decide "this is the
                    server I need for email/calendar/contact tasks" without seeing tool names.

                    {identitySection}{instructionsSection}Based on these tools:
                    {toolList}{promptSection}
                    Respond with only the summary, no preamble or explanation.
                    """;

                var messages = new[] { new ChatMessage(ChatRole.User, prompt) };
                var response = await _llmClient.GetResponseAsync(messages, options: null, cancellationToken: ct);
                summaryText = response.Text?.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate LLM summary for {ServerName}, using fallback", serverName);
            }
        }

        if (summaryText is null)
        {
            var toolsPart = tools.Count > 0
                ? $"Provides {tools.Count} tool(s): {string.Join(", ", toolNames.Take(10))}" +
                  (toolNames.Count > 10 ? $" and {toolNames.Count - 10} more." : ".")
                : "No tools available.";
            var promptsPart = prompts.Count > 0
                ? $" {prompts.Count} prompt template(s): {string.Join(", ", promptNames.Take(10))}" +
                  (promptNames.Count > 10 ? $" and {promptNames.Count - 10} more." : ".")
                : string.Empty;
            summaryText = toolsPart + promptsPart;
        }

        return new McpServerSummary
        {
            ServerName = serverName,
            Summary = summaryText,
            ToolCount = tools.Count,
            ToolNames = toolNames,
            PromptCount = prompts.Count,
            PromptNames = promptNames
        };
    }

    private async Task PublishServersIndexedAsync(
        List<McpServerSummary> servers,
        List<string> removedServers,
        CancellationToken ct)
    {
        var message = new McpServersIndexed
        {
            Servers = servers,
            RemovedServers = removedServers
        };

        var topic = $"tool.meta.mcp.{_agentName}";
        var envelope = message.ToEnvelope(
            source: $"mcp-bridge.{_agentName}",
            headers: new Dictionary<string, string>
            {
                [WellKnownHeaders.ContentTrust] = WellKnownHeaders.ContentTrustValues.System
            });

        await _publisher.PublishAsync(topic, envelope, ct);
    }

    private async Task<MessageResult> HandleToolInvokeAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var request = envelope.GetPayload<ToolInvokeRequest>();
        if (request is null)
        {
            _logger.LogWarning("Received tool invoke with null payload");
            return MessageResult.DeadLetter;
        }

        var replyTo = envelope.ReplyTo ?? $"tool.result.{_agentName}";

        // The call leases one snapshot of the server and uses only it: the client, tools, config,
        // elicitation coordinator and attachment gateway all come from the same connect, and the
        // client can't be disposed under the call by a concurrent reconnect.
        ServerLease? lease = null;

        if (envelope.Headers.TryGetValue(McpHeaders.ServerName, out var headerServer)
            && !string.IsNullOrEmpty(headerServer))
        {
            lease = _connections.Lease(headerServer);

            if (lease is null)
            {
                // Server is configured but not yet connected (e.g. tool call arrived during
                // startup before the background connection completed). Attempt an on-demand
                // connection so the call succeeds transparently rather than returning an error.
                if (_connections.TryGetConfig(headerServer, out var pendingConfig))
                {
                    _logger.LogInformation(
                        "MCP server '{Server}' is configured but not connected; connecting on demand before tool invoke",
                        headerServer);
                    await ConnectServerAsync(headerServer, pendingConfig, ct);
                    lease = _connections.Lease(headerServer);
                }

                if (lease is null)
                {
                    // A model that invents a server name needs the real ones to recover; one whose
                    // server is merely down needs to know retrying the name is right.
                    var error = new ToolError
                    {
                        ToolCallId = request.ToolCallId,
                        ToolName = request.ToolName,
                        Code = ToolError.Codes.ToolNotFound,
                        Message = _connections.IsConfigured(headerServer)
                            ? McpCallDiagnostics.DescribeUnavailableServer(headerServer)
                            : McpCallDiagnostics.DescribeUnknownServer(headerServer, _connections.ConfiguredNames),
                        IsRetryable = false
                    };
                    await PublishResponseAsync(error, replyTo, envelope.CorrelationId, ct);
                    return MessageResult.Ack;
                }
            }
        }
        else
        {
            // Fall back to searching by tool name
            var owner = _connections.Connected.FirstOrDefault(s => s.Tools.Any(t => t.Name == request.ToolName));
            if (owner is not null)
                lease = _connections.Lease(owner.Name);
        }

        if (lease is null)
        {
            var error = new ToolError
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Code = ToolError.Codes.ToolNotFound,
                Message = $"Tool '{request.ToolName}' not found on any connected MCP server",
                IsRetryable = false
            };

            await PublishResponseAsync(error, replyTo, envelope.CorrelationId, ct);
            return MessageResult.Ack;
        }

        using (lease)
        {
            return await InvokeOnServerAsync(envelope, request, replyTo, lease.Server, ct);
        }
    }

    private async Task<MessageResult> InvokeOnServerAsync(
        MessageEnvelope envelope, ToolInvokeRequest request, string replyTo, ConnectedServer server, CancellationToken ct)
    {
        var serverName = server.Name;

        // Only tools the server lists — after the operator's allowedTools/deniedTools filter — may
        // be called. Without this check a filtered-out tool was hidden from the model but still
        // callable by name, and an unknown name cost a reconnect-and-retry before failing with the
        // downstream's bare protocol error.
        var toolDefinition = await ResolveToolAsync(server, request.ToolName, ct);
        if (toolDefinition.Tool is null && toolDefinition.Available is { } available)
        {
            _logger.LogWarning("MCP {Server}/{Tool} refused: not among the server's {Count} available tool(s)",
                serverName, request.ToolName, available.Count);

            var error = new ToolError
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Code = ToolError.Codes.ToolNotFound,
                Message = McpCallDiagnostics.DescribeUnknownTool(serverName, request.ToolName, available),
                IsRetryable = false
            };

            await PublishResponseAsync(error, replyTo, envelope.CorrelationId, ct);
            return MessageResult.Ack;
        }

        var inputSchema = toolDefinition.Tool is { } resolvedTool
            && resolvedTool.JsonSchema.ValueKind != JsonValueKind.Undefined
                ? resolvedTool.JsonSchema.GetRawText()
                : null;

        // Parse timeout from headers — callers may request more time than the default (e.g. for
        // large MCP operations), so allow header values up to MaxTimeoutMs.
        var timeoutMs = _options.DefaultTimeoutMs;
        if (envelope.Headers.TryGetValue(WellKnownHeaders.TimeoutMs, out var timeoutStr)
            && int.TryParse(timeoutStr, out var parsedTimeout)
            && parsedTimeout > 0)
        {
            timeoutMs = Math.Min(parsedTimeout, _options.MaxTimeoutMs);
        }

        // A per-server timeout is authoritative for that MCP server.
        // This lets slow analytical MCPs opt into a larger budget while
        // ordinary MCPs retain the normal caller/default timeout.
        if (server.Config.ToolTimeoutMs is int serverTimeoutMs)
        {
            if (serverTimeoutMs <= 0)
            {
                _logger.LogWarning(
                    "Ignoring invalid ToolTimeoutMs={ToolTimeoutMs} for MCP server {Server}",
                    serverTimeoutMs,
                    serverName);
            }
            else
            {
                timeoutMs = Math.Min(
                    serverTimeoutMs,
                    _options.MaxTimeoutMs);
            }
        }

        _logger.LogInformation("→ MCP {Server}/{Tool} args={Args}",
            serverName, request.ToolName, request.Arguments ?? "(none)");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Parse and pre-process arguments outside the try block so they are accessible
        // in the catch for transparent reconnect-and-retry.
        Dictionary<string, object?> arguments;
        try
        {
            arguments = McpToolExecutor.ParseArguments(request.Arguments);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                "Invalid JSON arguments for {Server}/{Tool}: {Message} | Raw: {Args}",
                serverName, request.ToolName, ex.Message, request.Arguments);

            var parseError = new ToolError
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Code = ToolError.Codes.InvalidArguments,
                Message =
                    $"Tool arguments must be a valid JSON object with double-quoted keys and string values. " +
                    $"Received: {request.Arguments} — parse error: {ex.Message}. " +
                    $"Retry with correct JSON, for example: " +
                    $"{{\"timeZone\": \"America/Chicago\"}} not {{timeZone: 'America/Chicago'}}.",
                IsRetryable = false
            };

            await PublishResponseAsync(parseError, replyTo, envelope.CorrelationId, ct);
            return MessageResult.Ack;
        }

        // Detect and unwrap self-referential double-wrapped invoke_tool calls.
        if (request.ToolName == "invoke_tool"
            && GetStringArgument(arguments, "serverName") is { } wrappedServer
            && wrappedServer.Contains("aggregator", StringComparison.OrdinalIgnoreCase)
            && GetStringArgument(arguments, "toolName") == "invoke_tool"
            && GetStringArgument(arguments, "arguments") is { } innerArgsJson)
        {
            var unwrapped = McpToolExecutor.ParseArguments(innerArgsJson);
            if (unwrapped.Count > 0)
            {
                _logger.LogInformation(
                    "Unwrapping self-referential invoke_tool call (serverName={WrappedServer}); routing inner call: {InnerArgs}",
                    wrappedServer, innerArgsJson);
                arguments = unwrapped;
            }
        }

        // Apply per-server argument guards on the LLM's original arguments, BEFORE the
        // attachment gateway mutates them. Runs after the invoke_tool unwrap so guards
        // see the effective inner arguments. Fail closed: unresolvable guard config rejects.
        if (server.Config.ArgGuards.Count > 0)
        {
            var rejection = await McpArgGuardEvaluator.EvaluateAsync(
                _argGuards, serverName, server.Config, request.ToolName, arguments, ct);

            if (rejection is not null)
            {
                _logger.LogWarning("Arg guard rejected {Server}/{Tool}: {Reason}",
                    serverName, request.ToolName, rejection);

                var guardError = new ToolError
                {
                    ToolCallId = request.ToolCallId,
                    ToolName = request.ToolName,
                    Code = ToolError.Codes.InvalidArguments,
                    Message = rejection,
                    IsRetryable = false
                };

                await PublishResponseAsync(guardError, replyTo, envelope.CorrelationId, ct);
                return MessageResult.Ack;
            }
        }

        // Error hints describe what the model sent, not what the attachment rewrite below turns it into.
        var sentArguments = new Dictionary<string, object?>(arguments);

        // Apply attachment-passthrough request rewrite (no-op when the server has no manifest).
        // We capture ShouldRewriteResponse BEFORE RewriteRequestAsync because the rewrite
        // mutates the gateway-only `mode: "save"` to `stash`/`inline`.
        var attachmentGateway = server.AttachmentGateway.Value;
        var rewriteResponse = false;
        if (attachmentGateway is not null)
        {
            try
            {
                rewriteResponse = attachmentGateway.ShouldRewriteResponse(request.ToolName, arguments);
                await attachmentGateway.RewriteRequestAsync(request.ToolName, arguments, ct);
            }
            catch (Exception attachmentEx)
            {
                _logger.LogWarning(attachmentEx,
                    "Attachment gateway request rewrite failed for {Server}/{Tool}",
                    serverName, request.ToolName);

                var attachmentError = new ToolError
                {
                    ToolCallId = request.ToolCallId,
                    ToolName = request.ToolName,
                    Code = ToolError.Codes.InvalidArguments,
                    Message =
                        $"Attachment passthrough failed: {attachmentEx.Message}. " +
                        $"Verify each attachment path exists under the shared attachments directory.",
                    IsRetryable = false
                };

                await PublishResponseAsync(attachmentError, replyTo, envelope.CorrelationId, ct);
                return MessageResult.Ack;
            }
        }

        // Open before the call: an elicitation arrives on the MCP session's own message loop
        // while CallToolAsync is still awaiting, not on this async context, so the only way to
        // tie a question back to the call that provoked it is to record the call as in flight.
        using var elicitationScope = server.Elicitation
            ?.BeginCall(request.ToolName, request.Arguments, request.SessionId);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            var result = await server.Client.CallToolAsync(
                request.ToolName, arguments, cancellationToken: timeoutCts.Token);

            if (rewriteResponse && attachmentGateway is not null)
            {
                result = await attachmentGateway.RewriteResponseAsync(
                    request.ToolName, arguments, result, ct);
            }

            result = await CaptureBinaryContentAsync(serverName, server.Config, request.ToolName, result, ct);

            sw.Stop();
            var blocks = McpToolExecutor.MapContentBlocks(result);

            // A question the bridge declined usually means a thin or partial result. Say so in
            // the tool output, or the agent retries the identical call and gets the same answer.
            IReadOnlyList<McpElicitationRecord> elicitations = elicitationScope?.Records ?? [];
            if (McpElicitationNote.Build(elicitations, GetToolParameterNames(server, request.ToolName)) is { } elicitationNote)
                blocks = McpElicitationNote.AppendTo(blocks, elicitationNote);

            var content = blocks is not null ? McpToolExecutor.TextFromBlocks(blocks) : null;

            if (result.IsError == true)
            {
                _logger.LogWarning("← MCP {Server}/{Tool} ERROR in {ElapsedMs}ms: {Content}",
                    serverName, request.ToolName, sw.ElapsedMilliseconds, content);

                (blocks, content) = AppendArgumentHint(
                    serverName, request.ToolName, inputSchema, sentArguments, blocks, content);

                if (request.ToolName == "invoke_tool"
                    && GetStringArgument(arguments, "arguments") is { } innerArgs
                    && !innerArgs.TrimStart().StartsWith('{'))
                {
                    var targetTool = GetStringArgument(arguments, "toolName") ?? "the target tool";
                    content = (content ?? string.Empty) +
                        $"\n\nThe 'arguments' field must be a JSON object string, not a plain string. " +
                        $"Re-call invoke_tool with arguments formatted as a JSON object. " +
                        $"For example, if {targetTool} takes a 'message' parameter: " +
                        $"arguments = {{\"message\": \"{innerArgs}\"}}";
                    _logger.LogInformation(
                        "Appended invoke_tool arguments-format hint (inner args was a plain string)");
                }
            }
            else
            {
                var nonTextCount = blocks?.Count(b => b.Type != "text") ?? 0;
                if (nonTextCount > 0)
                    _logger.LogInformation("← MCP {Server}/{Tool} OK in {ElapsedMs}ms ({ContentLen} chars, {NonTextCount} non-text block(s))",
                        serverName, request.ToolName, sw.ElapsedMilliseconds, content?.Length ?? 0, nonTextCount);
                else
                    _logger.LogInformation("← MCP {Server}/{Tool} OK in {ElapsedMs}ms ({ContentLen} chars)",
                        serverName, request.ToolName, sw.ElapsedMilliseconds, content?.Length ?? 0);
            }

            var response = new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                ContentBlocks = blocks,
                Content = content,
                IsError = result.IsError == true
            };

            await PublishResponseAsync(response, replyTo, envelope.CorrelationId, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogWarning("← MCP {Server}/{Tool} TIMEOUT after {ElapsedMs}ms",
                serverName, request.ToolName, sw.ElapsedMilliseconds);

            var error = new ToolError
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Code = ToolError.Codes.Timeout,
                Message = $"MCP server '{serverName}' timed out after {timeoutMs}ms. " +
                          $"This is a transient error — retry the same tool call to continue."
                          + McpElicitationNote.DescribeDeclined(
                              elicitationScope?.Records ?? [], GetToolParameterNames(server, request.ToolName)),
                IsRetryable = true
            };

            await PublishResponseAsync(error, replyTo, envelope.CorrelationId, ct);
        }
        catch (McpProtocolException ex) when (ex.ErrorCode is McpErrorCode.InvalidParams or McpErrorCode.MethodNotFound)
        {
            // The server answered, so the connection is alive: reconnecting and retrying would only
            // repeat the rejection. Servers on other SDKs report argument validation this way rather
            // than as an isError result. Return it as a tool error the model can act on.
            sw.Stop();
            _logger.LogWarning("← MCP {Server}/{Tool} REJECTED in {ElapsedMs}ms ({Code}): {Message}",
                serverName, request.ToolName, sw.ElapsedMilliseconds, ex.ErrorCode, ex.Message);

            var hint = ex.Message.Contains("unknown tool", StringComparison.OrdinalIgnoreCase)
                ? McpCallDiagnostics.DescribeUnknownTool(
                    serverName, request.ToolName,
                    server.Tools.Select(t => t.Name).ToList())
                : McpCallDiagnostics.DescribeArgumentProblem(
                    serverName, request.ToolName, inputSchema, sentArguments, ex.Message);

            var response = new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = ex.Message
                          + (hint is null ? "" : "\n\n" + hint)
                          + McpElicitationNote.DescribeDeclined(
                              elicitationScope?.Records ?? [], GetToolParameterNames(server, request.ToolName)),
                IsError = true
            };

            await PublishResponseAsync(response, replyTo, envelope.CorrelationId, ct);
        }
        catch (Exception ex) when (FindReauthRequired(ex) is { } reauth)
        {
            sw.Stop();
            _logger.LogWarning(
                "← MCP {Server}/{Tool} REAUTH REQUIRED after {ElapsedMs}ms (code={Code}): {Detail}",
                serverName, request.ToolName, sw.ElapsedMilliseconds, reauth.Code, reauth.Message);

            // Silent refresh failed (or never consented). Reconnecting the MCP transport
            // will not help — the user must complete an interactive flow in the UI before
            // any Work IQ tool will succeed.
            var error = new ToolError
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Code = ToolError.Codes.AuthRequired,
                Message = BuildReauthRequiredMessage(reauth),
                IsRetryable = false
            };

            await PublishResponseAsync(error, replyTo, envelope.CorrelationId, ct);
            return MessageResult.Ack;
        }
        catch (Exception ex) when (FindAuthChallenge(ex) is { } authChallenge)
        {
            sw.Stop();
            _logger.LogWarning(
                "← MCP {Server}/{Tool} AUTH REQUIRED after {ElapsedMs}ms: {Detail}",
                serverName, request.ToolName, sw.ElapsedMilliseconds, authChallenge.Message);

            // The bearer token couldn't authenticate even after a forced refresh —
            // reconnecting will not help; the user must re-consent interactively.
            var error = new ToolError
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Code = ToolError.Codes.AuthRequired,
                Message = authChallenge.Message,
                IsRetryable = false
            };

            await PublishResponseAsync(error, replyTo, envelope.CorrelationId, ct);
            return MessageResult.Ack;
        }
        catch (Exception ex)
        {
            sw.Stop();

            // The first attempt is over. Close its scope before any retry: when the reconnect
            // fails quietly and keeps the same coordinator, a still-open scope would carry this
            // attempt's question count into the retry and list the call twice.
            elicitationScope?.Dispose();
            IReadOnlyList<McpElicitationRecord> failedElicitations = elicitationScope?.Records ?? [];

            // A failure right after a declined question is almost certainly the server giving
            // up without the value — not a dead connection. Retrying the identical call would
            // only be asked the same thing and declined again.
            var declinedBeforeFailure = failedElicitations.Any(r => !r.IsAccepted);

            // Any other exception from CallToolAsync likely means the connection is dead
            // (server restarted, session expired, network reset, etc.).
            // Reconnect synchronously and retry the call once so the agent never sees
            // a transient session failure — it's transparent from the agent's perspective.
            if (declinedBeforeFailure)
            {
                _logger.LogWarning(ex,
                    "← MCP {Server}/{Tool} FAILED after {ElapsedMs}ms following a declined elicitation — not retrying",
                    serverName, request.ToolName, sw.ElapsedMilliseconds);
            }
            else if (_connections.TryGetConfig(serverName, out var staleConfig))
            {
                _logger.LogWarning(ex,
                    "← MCP {Server}/{Tool} FAILED after {ElapsedMs}ms — reconnecting and retrying transparently",
                    serverName, request.ToolName, sw.ElapsedMilliseconds);

                McpElicitationCallScope? retryElicitationScope = null;
                ServerLease? retryLease = null;
                try
                {
                    // Reconnect only if nobody else has replaced the failed client meanwhile; if they
                    // have, retry on their connection instead of building yet another.
                    await ConnectServerAsync(serverName, staleConfig, ct, replacing: server.Client);

                    retryLease = _connections.Lease(serverName);
                    if (retryLease is not null && !ReferenceEquals(retryLease.Server.Client, server.Client))
                    {
                        var fresh = retryLease.Server;
                        using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        retryCts.CancelAfter(timeoutMs);

                        // A new connection comes with its own coordinator, so the retry needs a
                        // scope from it.
                        retryElicitationScope = fresh.Elicitation
                            ?.BeginCall(request.ToolName, request.Arguments, request.SessionId);

                        var retryResult = await fresh.Client.CallToolAsync(
                            request.ToolName, arguments, cancellationToken: retryCts.Token);

                        if (rewriteResponse && fresh.AttachmentGateway.Value is { } freshGateway)
                        {
                            retryResult = await freshGateway.RewriteResponseAsync(
                                request.ToolName, arguments, retryResult, ct);
                        }

                        retryResult = await CaptureBinaryContentAsync(
                            serverName, fresh.Config, request.ToolName, retryResult, ct);

                        sw.Stop();
                        var retryBlocks = McpToolExecutor.MapContentBlocks(retryResult);

                        IReadOnlyList<McpElicitationRecord> retryElicitations =
                            retryElicitationScope?.Records ?? [];
                        if (McpElicitationNote.Build(retryElicitations, GetToolParameterNames(fresh, request.ToolName)) is { } retryNote)
                            retryBlocks = McpElicitationNote.AppendTo(retryBlocks, retryNote);

                        var retryContent = retryBlocks is not null ? McpToolExecutor.TextFromBlocks(retryBlocks) : null;

                        if (retryResult.IsError == true)
                        {
                            (retryBlocks, retryContent) = AppendArgumentHint(
                                serverName, request.ToolName, inputSchema, sentArguments, retryBlocks, retryContent);
                        }

                        _logger.LogInformation(
                            "← MCP {Server}/{Tool} OK after transparent reconnect ({ContentLen} chars)",
                            serverName, request.ToolName, retryContent?.Length ?? 0);

                        var retryResponse = new ToolInvokeResponse
                        {
                            ToolCallId = request.ToolCallId,
                            ToolName = request.ToolName,
                            ContentBlocks = retryBlocks,
                            Content = retryContent,
                            IsError = retryResult.IsError == true
                        };

                        await PublishResponseAsync(retryResponse, replyTo, envelope.CorrelationId, ct);
                        return MessageResult.Ack;
                    }
                }
                catch (Exception retryEx)
                {
                    _logger.LogError(retryEx,
                        "Reconnect/retry for MCP {Server}/{Tool} also failed — returning error to agent",
                        serverName, request.ToolName);
                }
                finally
                {
                    retryElicitationScope?.Dispose();
                    if (retryElicitationScope is { HasRecords: true })
                        failedElicitations = [.. failedElicitations, .. retryElicitationScope.Records];
                    retryLease?.Dispose();
                }
            }
            else
            {
                _logger.LogError(ex, "← MCP {Server}/{Tool} FAILED after {ElapsedMs}ms",
                    serverName, request.ToolName, sw.ElapsedMilliseconds);
            }

            var error = new ToolError
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Code = ToolError.Codes.ExecutionFailed,
                Message = ex.Message
                          + McpElicitationNote.DescribeDeclined(
                              failedElicitations, GetToolParameterNames(server, request.ToolName)),
                IsRetryable = true
            };

            await PublishResponseAsync(error, replyTo, envelope.CorrelationId, ct);
        }

        return MessageResult.Ack;
    }

    private async Task<MessageResult> HandleManagementRequestAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var replyTo = envelope.ReplyTo;
        if (replyTo is null)
        {
            _logger.LogWarning("Management request from {Source} has no ReplyTo — cannot respond", envelope.Source);
            return MessageResult.DeadLetter;
        }

        if (envelope.MessageType == typeof(McpGetServiceDetailsRequest).FullName)
        {
            var req = envelope.GetPayload<McpGetServiceDetailsRequest>();
            if (req is null) return MessageResult.DeadLetter;

            _connections.TryGet(req.ServerName, out var server);
            IReadOnlyList<McpClientTool> tools = server?.Tools ?? [];
            IReadOnlyList<McpClientPrompt> serverPrompts = server?.Prompts ?? [];
            var metadata = server?.Metadata;
            var response = new McpGetServiceDetailsResponse
            {
                ServerName = req.ServerName,
                ServerId = server?.Config.Id,
                Fingerprint = server?.Summary.Fingerprint,
                ImplementationName = metadata?.ImplementationName,
                Title = metadata?.Title,
                Version = metadata?.Version,
                Description = metadata?.Description,
                Instructions = metadata?.Instructions,
                Tools = tools.Select(t => new McpToolDefinition
                {
                    Name = t.Name,
                    Description = t.Description ?? string.Empty,
                    ParametersSchema = t.JsonSchema.ValueKind != JsonValueKind.Undefined
                        ? t.JsonSchema.GetRawText()
                        : null
                }).ToList(),
                Prompts = serverPrompts.Select(ToPromptDefinition).ToList(),
                Error = server is not null ? null
                    : _connections.IsConfigured(req.ServerName)
                        ? McpCallDiagnostics.DescribeUnavailableServer(req.ServerName)
                        : McpCallDiagnostics.DescribeUnknownServer(req.ServerName, _connections.ConfiguredNames)
            };

            await PublishResponseAsync(response, replyTo, envelope.CorrelationId, ct);
        }
        else if (envelope.MessageType == typeof(McpRegisterServerRequest).FullName)
        {
            var req = envelope.GetPayload<McpRegisterServerRequest>();
            if (req is null) return MessageResult.DeadLetter;

            try
            {
                // A new name must be plain enough to become part of skill names, file paths and
                // typed tool names, and must not contain the server/tool separator.
                if (McpServerNames.Validate(req.ServerName) is { } nameError)
                {
                    await PublishResponseAsync(new McpRegisterServerResponse
                    {
                        ServerName = req.ServerName,
                        Success = false,
                        Error = nameError
                    }, replyTo, envelope.CorrelationId, ct);
                    return MessageResult.Ack;
                }

                // mcp_register_server is LLM-callable, and what it does is persisted. It only adds
                // new names: replacing an entry would drop whatever policy the operator gave it
                // (tool filters, auth, guards, elicitation...) and could re-point a trusted name,
                // credentials and all, at a URL of the model's choosing (#603).
                if (_connections.TryGetConfig(req.ServerName, out var existingConfig))
                {
                    await PublishResponseAsync(new McpRegisterServerResponse
                    {
                        ServerName = req.ServerName,
                        Success = false,
                        Error = existingConfig.IsAgentOwned()
                            ? $"An MCP server named '{req.ServerName}' is already registered. To point it somewhere " +
                              "else, remove it with mcp_unregister_server first."
                            : OperatorManaged(req.ServerName, "mcp_register_server")
                    }, replyTo, envelope.CorrelationId, ct);
                    return MessageResult.Ack;
                }

                var config = new McpBridgeServerConfig
                {
                    Id = McpServerNames.NewId(),
                    Origin = McpBridgeServerConfig.AgentOrigin,
                    Type = req.Type,
                    Url = req.Url,
                    Command = req.Command,
                    Args = req.Args,
                    Env = req.Env
                };

                // Validate guards before connecting so the caller gets a descriptive error
                // instead of the generic "Connection failed" (ConnectServerAsync fails closed
                // silently from the caller's perspective).
                var guardError = McpArgGuardEvaluator.ValidateConfig(_argGuards, req.ServerName, config);
                if (guardError is not null)
                {
                    var guardResponse = new McpRegisterServerResponse
                    {
                        ServerName = req.ServerName,
                        Success = false,
                        Error = $"Invalid argGuards configuration: {guardError}"
                    };
                    await PublishResponseAsync(guardResponse, replyTo, envelope.CorrelationId, ct);
                    return MessageResult.Ack;
                }

                // Reject registrations that duplicate an existing server's URL and credentials
                // under a different name. The name doesn't matter for dedup — URL + headers +
                // transport + command/args/env must all match for this to be considered a dup.
                var newIdentity = config.CanonicalIdentity();
                if (!string.IsNullOrEmpty(newIdentity))
                {
                    var existingDup = _connections.ConfiguredNames.FirstOrDefault(name =>
                        !string.Equals(name, req.ServerName, StringComparison.OrdinalIgnoreCase)
                        && _connections.TryGetConfig(name, out var other)
                        && string.Equals(other.CanonicalIdentity(), newIdentity, StringComparison.Ordinal));

                    if (existingDup is not null)
                    {
                        var dupResponse = new McpRegisterServerResponse
                        {
                            ServerName = req.ServerName,
                            Success = false,
                            Error = $"An MCP server with the same URL and credentials is already registered as '{existingDup}'. " +
                                    $"Use the existing registration, or unregister it before registering under a different name."
                        };
                        await PublishResponseAsync(dupResponse, replyTo, envelope.CorrelationId, ct);
                        return MessageResult.Ack;
                    }
                }

                await ConnectServerAsync(req.ServerName, config, ct);
                await PersistServerConfigAsync(req.ServerName, config, remove: false);

                var connected = _connections.TryGet(req.ServerName, out var registered);
                var response = new McpRegisterServerResponse
                {
                    ServerName = req.ServerName,
                    Success = connected,
                    Summary = connected ? $"{registered!.Tools.Count} tool(s) available." : null,
                    Error = connected ? null : "Connection failed"
                };

                await PublishResponseAsync(response, replyTo, envelope.CorrelationId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register server {ServerName}", req.ServerName);
                var response = new McpRegisterServerResponse
                {
                    ServerName = req.ServerName,
                    Success = false,
                    Error = ex.Message
                };
                await PublishResponseAsync(response, replyTo, envelope.CorrelationId, ct);
            }
        }
        else if (envelope.MessageType == typeof(McpUnregisterServerRequest).FullName)
        {
            var req = envelope.GetPayload<McpUnregisterServerRequest>();
            if (req is null) return MessageResult.DeadLetter;

            try
            {
                // Only an entry the agent registered, and the operator hasn't since given policy,
                // may be removed this way. Removing an operator's entry would shed its policy, and
                // registering the name again would bring it back without any (#603).
                var refusal = !_connections.TryGetConfig(req.ServerName, out var existing)
                    ? McpCallDiagnostics.DescribeUnknownServer(req.ServerName, _connections.ConfiguredNames)
                    : existing.IsAgentOwned() ? null : OperatorManaged(req.ServerName, "mcp_unregister_server");
                if (refusal is not null)
                {
                    await PublishResponseAsync(new McpUnregisterServerResponse
                    {
                        ServerName = req.ServerName,
                        Success = false,
                        Error = refusal
                    }, replyTo, envelope.CorrelationId, ct);
                    return MessageResult.Ack;
                }

                await DisconnectServerAsync(req.ServerName);
                await PersistServerConfigAsync(req.ServerName, null, remove: true);

                var response = new McpUnregisterServerResponse
                {
                    ServerName = req.ServerName,
                    Success = true
                };
                await PublishResponseAsync(response, replyTo, envelope.CorrelationId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unregister server {ServerName}", req.ServerName);
                var response = new McpUnregisterServerResponse
                {
                    ServerName = req.ServerName,
                    Success = false,
                    Error = ex.Message
                };
                await PublishResponseAsync(response, replyTo, envelope.CorrelationId, ct);
            }
        }
        else if (envelope.MessageType == typeof(McpGetPromptRequest).FullName)
        {
            var req = envelope.GetPayload<McpGetPromptRequest>();
            if (req is null) return MessageResult.DeadLetter;

            using var lease = _connections.Lease(req.ServerName);
            var precheckError = lease is null
                ? _connections.IsConfigured(req.ServerName)
                    ? McpCallDiagnostics.DescribeUnavailableServer(req.ServerName)
                    : McpCallDiagnostics.DescribeUnknownServer(req.ServerName, _connections.ConfiguredNames)
                : CheckPromptCall(lease.Server, req);

            if (lease is null || precheckError is not null)
            {
                var rejected = new McpGetPromptResponse
                {
                    ServerName = req.ServerName,
                    PromptName = req.PromptName,
                    Error = precheckError
                };
                await PublishResponseAsync(rejected, replyTo, envelope.CorrelationId, ct);
                return MessageResult.Ack;
            }

            IReadOnlyDictionary<string, object?> promptArgs =
                req.Arguments.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);

            try
            {
                var result = await lease.Server.Client.GetPromptAsync(req.PromptName, promptArgs, cancellationToken: ct);
                await PublishResponseAsync(ToPromptResponse(req, result), replyTo, envelope.CorrelationId, ct);
            }
            catch (McpProtocolException ex) when (ex.ErrorCode is McpErrorCode.InvalidParams or McpErrorCode.MethodNotFound)
            {
                // The server answered: a reconnect would only repeat the rejection.
                _logger.LogWarning("GetPrompt {Server}/{Prompt} REJECTED ({Code}): {Message}",
                    req.ServerName, req.PromptName, ex.ErrorCode, ex.Message);

                var rejected = new McpGetPromptResponse
                {
                    ServerName = req.ServerName,
                    PromptName = req.PromptName,
                    Error = ex.Message
                };
                await PublishResponseAsync(rejected, replyTo, envelope.CorrelationId, ct);
            }
            catch (Exception ex)
            {
                // Attempt reconnect and retry once (same pattern as HandleToolInvokeAsync): only if
                // nobody else replaced the failed client meanwhile, and only on a new connection.
                if (_connections.TryGetConfig(req.ServerName, out var staleConfig))
                {
                    _logger.LogWarning(ex,
                        "GetPrompt {Server}/{Prompt} FAILED — reconnecting and retrying transparently",
                        req.ServerName, req.PromptName);
                    try
                    {
                        await ConnectServerAsync(req.ServerName, staleConfig, ct, replacing: lease.Server.Client);
                        using var retryLease = _connections.Lease(req.ServerName);
                        if (retryLease is not null && !ReferenceEquals(retryLease.Server.Client, lease.Server.Client))
                        {
                            var retryResult = await retryLease.Server.Client.GetPromptAsync(
                                req.PromptName, promptArgs, cancellationToken: ct);
                            await PublishResponseAsync(ToPromptResponse(req, retryResult), replyTo, envelope.CorrelationId, ct);
                            return MessageResult.Ack;
                        }
                    }
                    catch (Exception retryEx)
                    {
                        _logger.LogError(retryEx,
                            "Reconnect/retry for GetPrompt {Server}/{Prompt} also failed",
                            req.ServerName, req.PromptName);
                    }
                }

                var errorResponse = new McpGetPromptResponse
                {
                    ServerName = req.ServerName,
                    PromptName = req.PromptName,
                    Error = ex.Message
                };
                await PublishResponseAsync(errorResponse, replyTo, envelope.CorrelationId, ct);
            }
        }
        else
        {
            _logger.LogWarning("Unknown management message type: {MessageType}", envelope.MessageType);
        }

        return MessageResult.Ack;
    }

    private async Task PersistServerConfigAsync(string name, McpBridgeServerConfig? config, bool remove)
    {
        _logger.LogInformation(
            "PersistServerConfigAsync called: server={ServerName}, remove={Remove}",
            name, remove);

        await _configPersistLock.WaitAsync();
        try
        {
            McpBridgeConfig current;
            string? existingJson = null;

            if (File.Exists(_configPath))
            {
                existingJson = await File.ReadAllTextAsync(_configPath);
                current = JsonSerializer.Deserialize<McpBridgeConfig>(existingJson, JsonOptions)
                    ?? new McpBridgeConfig();

                // If deserialization returned null or empty McpServers but the file was non-empty,
                // the file may be corrupt — try the backup before proceeding.
                if (current.McpServers.Count == 0 && existingJson.Trim().Length > 0)
                {
                    _logger.LogWarning(
                        "Config file at {Path} deserialized to empty McpServers but file was non-empty ({Length} chars) — attempting backup recovery",
                        _configPath, existingJson.Length);

                    var backupConfig = await TryLoadFromBackupAsync(CancellationToken.None);
                    if (backupConfig is not null)
                    {
                        current = backupConfig;
                    }
                    else
                    {
                        _logger.LogError(
                            "Both config and backup failed to provide valid McpServers — aborting persist for {ServerName} to avoid data loss",
                            name);
                        return;
                    }
                }
            }
            else
            {
                _logger.LogWarning(
                    "Config file does not exist at {Path} during persist — creating new config",
                    _configPath);
                current = new McpBridgeConfig();
            }

            _logger.LogInformation(
                "PersistServerConfigAsync BEFORE modification: servers=[{Servers}]",
                string.Join(", ", current.McpServers.Keys));

            if (remove)
                current.McpServers.Remove(name);
            else if (config is not null)
                current.McpServers[name] = config;

            _logger.LogInformation(
                "PersistServerConfigAsync AFTER modification: servers=[{Servers}]",
                string.Join(", ", current.McpServers.Keys));

            var updated = JsonSerializer.Serialize(current, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            // Create backup of the current file before writing (only if it exists and had content)
            if (existingJson is not null)
            {
                var backupPath = _configPath + ".bak";
                await File.WriteAllTextAsync(backupPath, existingJson);
            }

            // Write to temp file first, then atomically replace to prevent corruption on crash
            var tempPath = _configPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, updated);
            File.Move(tempPath, _configPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist server config change for {ServerName}", name);
        }
        finally
        {
            _configPersistLock.Release();
        }
    }

    /// <summary>
    /// Parameter names from a tool's discovered input schema, so an elicitation note can say
    /// whether a declined field is something the agent can pass on retry. Null when the schema
    /// is unknown, or when the tool is an <c>invoke_tool</c> dispatcher whose real parameters
    /// belong to the inner tool.
    /// </summary>
    private static IReadOnlyCollection<string>? GetToolParameterNames(ConnectedServer server, string toolName)
    {
        if (toolName == "invoke_tool"
            || server.Tools.FirstOrDefault(t => t.Name == toolName) is not { } tool
            || tool.JsonSchema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!tool.JsonSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return [.. properties.EnumerateObject().Select(p => p.Name)];
    }

    private async Task PublishResponseAsync<T>(
        T payload,
        string topic,
        string? correlationId,
        CancellationToken ct)
    {
        var envelope = payload.ToEnvelope(
            source: $"mcp-bridge.{_agentName}",
            correlationId: correlationId,
            headers: new Dictionary<string, string>
            {
                [WellKnownHeaders.ContentTrust] = WellKnownHeaders.ContentTrustValues.ToolOutput,
                [WellKnownHeaders.ToolProvider] = "mcp"
            });

        await _publisher.PublishAsync(topic, envelope, ct);
    }

    private async Task<MessageResult> HandleRefreshRequestAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Timestamp < _startupCompletedAt)
        {
            _logger.LogDebug(
                "Ignoring stale MCP refresh request from {Source} (sent at {Sent}, startup completed at {Ready})",
                envelope.Source, envelope.Timestamp, _startupCompletedAt);
            return MessageResult.Ack;
        }

        var request = envelope.GetPayload<McpMetadataRefreshRequest>();

        if (request?.ServerName is not null)
        {
            if (_connections.TryGetConfig(request.ServerName, out var config))
            {
                // Reconnect rather than refresh the stale client — this handles server restarts.
                await ConnectServerAsync(request.ServerName, config, ct);
            }
        }
        else
        {
            foreach (var name in _connections.Connected.Select(s => s.Name))
            {
                if (!_connections.TryGetConfig(name, out var config)) continue;

                try
                {
                    // Reconnect to pick up any server restarts that happened since startup.
                    // ConnectServerAsync handles its own summary publication.
                    await ConnectServerAsync(name, config, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to refresh/reconnect MCP server {Name} — skipping", name);
                }
            }
        }

        return MessageResult.Ack;
    }

    private async Task RunReconnectSweepAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_options.ReconnectSweepIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var disconnected = _connections.ConfiguredNames
                    .Where(name => !_connections.IsConnected(name))
                    .ToList();

                foreach (var name in disconnected)
                {
                    if (!_connections.TryGetConfig(name, out var config)) continue;

                    _logger.LogInformation(
                        "Reconnect sweep: attempting to reconnect MCP server {Name}", name);
                    try
                    {
                        // Another caller may connect it while this one waits for the lock.
                        await ConnectServerAsync(name, config, ct, onlyIfDisconnected: true);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Reconnect sweep: failed to reconnect MCP server {Name}", name);
                    }
                }

                await RefreshStaleSurfacesAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// Re-reads the surface of every connected server not checked within
    /// <see cref="McpBridgeOptions.SurfaceRefreshIntervalSeconds"/>. This is the backstop for
    /// servers that change their tools without sending list_changed — including stateless HTTP
    /// servers, which have no stream to send it on.
    /// </summary>
    private async Task RefreshStaleSurfacesAsync(CancellationToken ct)
    {
        if (_options.SurfaceRefreshIntervalSeconds <= 0) return;

        var interval = TimeSpan.FromSeconds(_options.SurfaceRefreshIntervalSeconds);
        var now = DateTimeOffset.UtcNow;
        foreach (var name in _connections.Connected.Select(s => s.Name))
        {
            if (_lastSurfaceCheck.TryGetValue(name, out var last) && now - last < interval)
                continue;

            try
            {
                await RefreshSurfaceAsync(name, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Surface refresh for MCP server {Name} failed", name);
            }
        }
    }

    private void SetupConfigWatcher()
    {
        var directory = Path.GetDirectoryName(_configPath);

        if (directory is null) return;

        var fileName = Path.GetFileName(_configPath);
        _configWatcher = new FileSystemWatcher(directory, fileName)
        {
            // Include Size + FileName and subscribe to Renamed so rename-into-place
            // writes (editors, `kubectl cp`, our own File.Move persist path) are seen —
            // these were silently missed before (issue #470). The config poll is the
            // belt-and-suspenders fallback when inotify doesn't fire at all.
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _configWatcher.Changed += OnConfigFileChanged;
        _configWatcher.Created += OnConfigFileChanged;
        _configWatcher.Renamed += OnConfigFileChanged;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        => TriggerReload($"watcher:{e.ChangeType}");

    /// <summary>
    /// Coordinates config reloads from both the <see cref="FileSystemWatcher"/> and the
    /// poll loop. Debounces bursts (a single save fires multiple events) to 500 ms and
    /// guards against overlapping reloads via <see cref="_reloadPending"/>.
    /// </summary>
    private void TriggerReload(string reason)
    {
        if (Interlocked.Exchange(ref _reloadPending, 1) != 0)
            return;

        _logger.LogInformation("MCP config change detected ({Reason}), reloading...", reason);
        _reloadDebounce?.Dispose();
        _reloadDebounce = new Timer(
            _ => _ = ReloadConfigAsync(),
            null,
            TimeSpan.FromMilliseconds(500),
            Timeout.InfiniteTimeSpan);
    }

    private async Task ReloadConfigAsync()
    {
        try
        {
            await LoadConfigAndConnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reloading MCP config");
        }
        finally
        {
            Interlocked.Exchange(ref _reloadPending, 0);
        }
    }

    /// <summary>
    /// Polling fallback for config changes. The <see cref="FileSystemWatcher"/> can miss
    /// changes entirely on some network/overlay filesystems (e.g. Longhorn PVCs), so we
    /// also stat the file's last-write time and size on an interval and reload when it
    /// differs from the last-seen stamp. See issue #470.
    /// </summary>
    private async Task RunConfigPollAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_options.ConfigPollIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (ConfigChangedSinceLastSeen())
                    TriggerReload("poll");
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// Last-write time + length of the config file — cheap to read and sufficient to
    /// detect operator edits. Stored after each load so the bridge's own writes don't
    /// re-trigger a reload.
    /// </summary>
    internal readonly record struct ConfigStamp(DateTime LastWriteUtc, long Length);

    /// <summary>Reads the current <see cref="ConfigStamp"/>, or null if the file is absent/unreadable.</summary>
    internal static ConfigStamp? ReadConfigStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new ConfigStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when the config file's stamp differs from the last-seen value. Updates the
    /// last-seen stamp as a side effect so a persistently-unreadable/corrupt file is only
    /// retried when it actually changes again, not on every poll tick.
    /// </summary>
    private bool ConfigChangedSinceLastSeen()
    {
        var current = ReadConfigStamp(_configPath);
        if (current is null)
            return false;

        lock (_stampGate)
        {
            if (_lastConfigStamp is { } last && current.Value == last)
                return false;

            _lastConfigStamp = current;
            return true;
        }
    }

    private Task DisposeClientsAsync() => _connections.RetireAllAsync(ShutdownGrace);

    /// <summary>
    /// Walks the exception chain looking for an <see cref="McpAuthChallengeException"/>.
    /// The MCP client library may wrap the bearer handler's exception inside a transport
    /// or protocol exception, so we search inner exceptions rather than relying on the
    /// outer type.
    /// </summary>
    private static McpAuthChallengeException? FindAuthChallenge(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is McpAuthChallengeException auth) return auth;
        }
        return null;
    }

    /// <summary>
    /// Walks the exception chain looking for a <see cref="TokenAcquisitionException"/>
    /// whose code indicates that the user must complete an interactive auth flow
    /// (initial consent never happened, or refresh-token rotation failed). Surfaces
    /// as a dedicated <c>auth_required</c> tool error so the LLM stops retrying and
    /// reports a clear actionable message instead.
    /// </summary>
    internal static TokenAcquisitionException? FindReauthRequired(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is TokenAcquisitionException tae
                && (tae.Code == TokenAcquisitionException.Codes.ReauthRequired
                    || tae.Code == TokenAcquisitionException.Codes.NotAuthenticated))
            {
                return tae;
            }
        }
        return null;
    }

    /// <summary>
    /// Maps a <see cref="TokenAcquisitionException"/> identified by
    /// <see cref="FindReauthRequired"/> to the user-facing tool-error message.
    /// Split out so unit tests can assert wording without spinning up the whole
    /// bridge.
    /// </summary>
    internal static string BuildReauthRequiredMessage(TokenAcquisitionException reauth) =>
        reauth.Code == TokenAcquisitionException.Codes.NotAuthenticated
            ? "Microsoft 365 has not been connected yet. Open the Blazor app and click "
              + "'Connect M365' to complete the initial sign-in. Work IQ tools will fail "
              + "until consent is granted."
            : "Microsoft 365 connection has expired. Open the Blazor app and click "
              + "'Reconnect M365' to restore access. Work IQ tools will fail until "
              + "reconnection is complete.";

    /// <summary>
    /// True when <paramref name="config"/> requires the WorkIQ auth profile and the
    /// health tracker reports the cache cannot currently produce tokens. Callers use
    /// this to suppress publishing the server's tools to the agent — so the LLM never
    /// sees them and never makes calls that would just bounce back as auth_required.
    /// </summary>
    private bool IsServerHiddenByAuth(McpBridgeServerConfig config)
    {
        if (_healthTracker is null) return false;
        if (config.Auth is null) return false;
        if (!string.Equals(config.Auth.Profile, "workiq", StringComparison.OrdinalIgnoreCase))
            return false;
        return !_healthTracker.IsHealthy;
    }

    /// <summary>
    /// Handler for <see cref="WorkIqHealthTracker.HealthChanged"/>. On the flip to
    /// healthy, re-publish cached summaries for every workiq server so the agent
    /// re-includes their tools. On the flip to unhealthy, publish removal entries
    /// so the agent drops those tools from its working set.
    /// </summary>
    private void OnAuthHealthChanged(WorkIqHealthTracker.HealthChangedArgs args)
    {
        // Races are tolerated: a server added or removed mid-flip just gets the next publish cycle.
        var workiqServers = _connections.ConfiguredNames
            .Where(name => _connections.TryGetConfig(name, out var config)
                && string.Equals(config.Auth?.Profile, "workiq", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (workiqServers.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (args.NewValue)
                {
                    // Healthy again — re-publish each workiq server's cached summary.
                    foreach (var name in workiqServers)
                    {
                        if (_connections.TryGet(name, out var server))
                        {
                            await PublishServersIndexedAsync([server.Summary], [], CancellationToken.None);
                        }
                    }
                }
                else
                {
                    // Unhealthy — tell the agent to drop these from its tool list.
                    await PublishServersIndexedAsync([], workiqServers, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to republish MCP tool list after WorkIQ health flip to {New}",
                    args.NewValue);
            }
        });
    }

    /// <summary>
    /// Expands <c>${VAR_NAME}</c> placeholders in <paramref name="value"/> using
    /// <see cref="Environment.GetEnvironmentVariable"/>. Unset variables expand to an empty string.
    /// </summary>
    private static string ExpandEnvVars(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            value,
            @"\$\{([^}]+)\}",
            m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? string.Empty);

    /// <summary>
    /// Extracts a string value from a parsed argument dictionary, handling
    /// the <see cref="JsonElement"/> boxing that System.Text.Json produces
    /// when deserializing to <c>Dictionary&lt;string, object?&gt;</c>.
    /// </summary>
    private static string? GetStringArgument(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val)) return null;
        return val switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            JsonElement je => je.GetRawText(),
            string s => s,
            _ => val?.ToString()
        };
    }

    public async ValueTask DisposeAsync()
    {
        _configWatcher?.Dispose();
        _reloadDebounce?.Dispose();

        if (_healthTracker is not null)
            _healthTracker.HealthChanged -= OnAuthHealthChanged;

        if (_sweepCts is not null)
        {
            await _sweepCts.CancelAsync();
            if (_reconnectSweepTask is not null)
                await _reconnectSweepTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (_configPollTask is not null)
                await _configPollTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _sweepCts.Dispose();
        }

        await DisposeClientsAsync();

        if (_invokeSubscription is not null)
            await _invokeSubscription.DisposeAsync();
        if (_refreshSubscription is not null)
            await _refreshSubscription.DisposeAsync();
        if (_manageSubscription is not null)
            await _manageSubscription.DisposeAsync();

        _configPersistLock.Dispose();
    }
}
