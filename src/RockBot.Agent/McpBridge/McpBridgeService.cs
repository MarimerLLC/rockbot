using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, McpBridgeServerConfig> _serverConfigs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<McpClientTool>> _serverTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<McpClientPrompt>> _serverPrompts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, McpServerMetadata> _serverMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, McpServerSummary> _serverSummaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AttachmentGatewayEntry> _attachmentGateways = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<IAttachmentStorage> _attachmentStorage = new(() => new AttachmentStorage());

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
        IMcpArgGuardRegistry? argGuards = null)
    {
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

        if (seeded || removedDupes > 0)
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
                    "Persisted MCP config changes to {Path} (seeded={Seeded}, duplicatesRemoved={Removed})",
                    _configPath, seeded, removedDupes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist seeded MCP servers to {Path}", _configPath);
            }
        }

        // Disconnect servers that are no longer in config
        var removedServers = _clients.Keys.Except(config.McpServers.Keys).ToList();
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
        var didSelfWrite = seeded || removedDupes > 0;
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

    private async Task ConnectServerAsync(string name, McpBridgeServerConfig config, CancellationToken ct)
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
        // silently weaken it. The server never lands in _serverConfigs, so tool invokes
        // get server-not-found until the config is fixed. Outside the retry loop below —
        // a config error is not transient.
        var guardConfigError = McpArgGuardEvaluator.ValidateConfig(_argGuards, name, config);
        if (guardConfigError is not null)
        {
            _logger.LogError(
                "MCP server {Name} has invalid argGuards configuration — refusing to connect (fail closed): {Error}",
                name, guardConfigError);
            return;
        }

        // Store config before attempting connection so the reconnect sweep can
        // retry servers that never connected successfully at startup. Invalidate any
        // cached attachment gateway so the next call rebuilds it against the fresh
        // URL/headers/manifest.
        _serverConfigs[name] = config;
        InvalidateAttachmentGateway(name);

        var maxAttempts = 1 + Math.Max(0, _options.ConnectRetryCount);
        var delayMs = _options.ConnectRetryBaseDelayMs;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
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

                var newClient = await McpClient.CreateAsync(transport, cancellationToken: ct);

                // Discover tools before committing the swap so a failure leaves the old client intact
                var tools = await newClient.ListToolsAsync(cancellationToken: ct);
                var filteredTools = ApplyToolFilters(tools.ToList(), config);

                List<McpClientPrompt> prompts = [];
                try
                {
                    var rawPrompts = await newClient.ListPromptsAsync(cancellationToken: ct);
                    prompts = [.. rawPrompts];
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "MCP server {Name} does not support prompts or listing failed", name);
                }

                // Connection succeeded — atomically replace the old client without publishing a removal
                if (_clients.Remove(name, out var oldClient))
                {
                    try { await oldClient.DisposeAsync(); }
                    catch { /* Best-effort cleanup */ }
                }

                _clients[name] = newClient;
                _serverTools[name] = filteredTools;
                _serverPrompts[name] = prompts;

                var serverInfo = newClient.ServerInfo;
                var metadata = new McpServerMetadata(
                    ImplementationName: serverInfo?.Name,
                    Title: serverInfo?.Title,
                    Version: serverInfo?.Version,
                    Description: serverInfo?.Description,
                    Instructions: newClient.ServerInstructions);
                _serverMetadata[name] = metadata;

                _logger.LogInformation(
                    "Connected to MCP server {Name} (impl={ImplName} v{Version}) with {ToolCount} tools and {PromptCount} prompts",
                    name, metadata.ImplementationName ?? "(unknown)", metadata.Version ?? "(unknown)",
                    filteredTools.Count, prompts.Count);

                // Build summary and cache so a future health flip can re-publish without
                // re-running the (LLM-driven) summary generation.
                var summary = await GenerateSummaryAsync(name, metadata, filteredTools, prompts, ct);
                _serverSummaries[name] = summary;

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
                throw;
            }
            catch (Exception ex)
            {
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
    }

    private async Task DisconnectServerAsync(string name)
    {
        if (_clients.Remove(name, out var client))
        {
            try { await client.DisposeAsync(); }
            catch { /* Best-effort cleanup */ }
        }

        _serverTools.Remove(name);
        _serverPrompts.Remove(name);
        _serverMetadata.Remove(name);
        _serverConfigs.Remove(name);
        _serverSummaries.Remove(name);
        InvalidateAttachmentGateway(name);

        await PublishServersIndexedAsync([], [name], CancellationToken.None);

        _logger.LogInformation("Disconnected from MCP server {Name}", name);
    }

    private AttachmentGateway? GetOrCreateAttachmentGateway(string serverName)
    {
        if (!_serverConfigs.TryGetValue(serverName, out var config)) return null;
        if (config.Attachments is null) return null;
        if (string.IsNullOrEmpty(config.Url)) return null;

        if (_attachmentGateways.TryGetValue(serverName, out var entry))
            return entry.Gateway;

        // Reuse the same client-construction logic so attachment uploads carry
        // the same auth and headers as MCP tool calls.
        var http = TryBuildHttpClient(serverName, config) ?? new HttpClient();

        var gateway = new AttachmentGateway(
            _attachmentStorage.Value,
            http,
            new Uri(config.Url),
            config.Attachments,
            _logger);

        _attachmentGateways[serverName] = new AttachmentGatewayEntry(gateway, http);
        return gateway;
    }

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
        string toolName,
        CallToolResult result,
        CancellationToken ct)
    {
        _serverConfigs.TryGetValue(serverName, out var config);
        return _binaryCapture.Value.CaptureAsync(
            serverName, toolName, result, config?.Attachments?.Capture, ct);
    }

    private void InvalidateAttachmentGateway(string serverName)
    {
        if (_attachmentGateways.Remove(serverName, out var entry))
        {
            try { entry.HttpClient.Dispose(); }
            catch { /* Best-effort cleanup */ }
        }
    }

    private sealed record AttachmentGatewayEntry(AttachmentGateway Gateway, HttpClient HttpClient);

    /// <summary>
    /// Snapshot of a connected MCP server's self-reported identity, captured once at connect
    /// from <see cref="McpClient.ServerInfo"/> and <see cref="McpClient.ServerInstructions"/>.
    /// Forwarded to agents via <see cref="McpGetServiceDetailsResponse"/> and used as input
    /// to the LLM-generated server summary.
    /// </summary>
    private sealed record McpServerMetadata(
        string? ImplementationName,
        string? Title,
        string? Version,
        string? Description,
        string? Instructions);

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

    private async Task<McpServerSummary> GenerateSummaryAsync(
        string serverName,
        McpServerMetadata metadata,
        List<McpClientTool> tools,
        List<McpClientPrompt> prompts,
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

        // Check for direct server routing via rb-mcp-server header
        string? serverName = null;
        McpClient? client = null;

        if (envelope.Headers.TryGetValue(McpHeaders.ServerName, out var headerServer)
            && !string.IsNullOrEmpty(headerServer))
        {
            serverName = headerServer;
            client = _clients.GetValueOrDefault(headerServer);

            if (client is null)
            {
                // Server is configured but not yet connected (e.g. tool call arrived during
                // startup before the background connection completed). Attempt an on-demand
                // connection so the call succeeds transparently rather than returning an error.
                if (_serverConfigs.TryGetValue(headerServer, out var pendingConfig))
                {
                    _logger.LogInformation(
                        "MCP server '{Server}' is configured but not connected; connecting on demand before tool invoke",
                        headerServer);
                    await ConnectServerAsync(headerServer, pendingConfig, ct);
                    client = _clients.GetValueOrDefault(headerServer);
                }

                if (client is null)
                {
                    var error = new ToolError
                    {
                        ToolCallId = request.ToolCallId,
                        ToolName = request.ToolName,
                        Code = ToolError.Codes.ToolNotFound,
                        Message = $"MCP server '{headerServer}' is not connected",
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
            foreach (var (name, tools) in _serverTools)
            {
                if (tools.Any(t => t.Name == request.ToolName))
                {
                    serverName = name;
                    client = _clients.GetValueOrDefault(name);
                    break;
                }
            }
        }

        if (client is null || serverName is null)
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
        if (!string.IsNullOrWhiteSpace(serverName)
            && _serverConfigs.TryGetValue(serverName, out var timeoutConfig)
            && timeoutConfig.ToolTimeoutMs is int serverTimeoutMs)
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
        if (_serverConfigs.TryGetValue(serverName, out var invokeConfig) && invokeConfig.ArgGuards.Count > 0)
        {
            var rejection = await McpArgGuardEvaluator.EvaluateAsync(
                _argGuards, serverName, invokeConfig, request.ToolName, arguments, ct);

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

        // Apply attachment-passthrough request rewrite (no-op when the server has no manifest).
        // We capture ShouldRewriteResponse BEFORE RewriteRequestAsync because the rewrite
        // mutates the gateway-only `mode: "save"` to `stash`/`inline`.
        var attachmentGateway = GetOrCreateAttachmentGateway(serverName);
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

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            var result = await client.CallToolAsync(
                request.ToolName, arguments, cancellationToken: timeoutCts.Token);

            if (rewriteResponse && attachmentGateway is not null)
            {
                result = await attachmentGateway.RewriteResponseAsync(
                    request.ToolName, arguments, result, ct);
            }

            result = await CaptureBinaryContentAsync(serverName, request.ToolName, result, ct);

            sw.Stop();
            var blocks = McpToolExecutor.MapContentBlocks(result);
            var content = blocks is not null ? McpToolExecutor.TextFromBlocks(blocks) : null;

            if (result.IsError == true)
            {
                _logger.LogWarning("← MCP {Server}/{Tool} ERROR in {ElapsedMs}ms: {Content}",
                    serverName, request.ToolName, sw.ElapsedMilliseconds, content);

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
                          $"This is a transient error — retry the same tool call to continue.",
                IsRetryable = true
            };

            await PublishResponseAsync(error, replyTo, envelope.CorrelationId, ct);
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

            // Any exception from CallToolAsync likely means the connection is dead
            // (server restarted, session expired, network reset, etc.).
            // Reconnect synchronously and retry the call once so the agent never sees
            // a transient session failure — it's transparent from the agent's perspective.
            if (serverName is not null && _serverConfigs.TryGetValue(serverName, out var staleConfig))
            {
                _logger.LogWarning(ex,
                    "← MCP {Server}/{Tool} FAILED after {ElapsedMs}ms — reconnecting and retrying transparently",
                    serverName, request.ToolName, sw.ElapsedMilliseconds);

                try
                {
                    await ConnectServerAsync(serverName, staleConfig, ct);

                    var freshClient = _clients.GetValueOrDefault(serverName);
                    if (freshClient is not null)
                    {
                        using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        retryCts.CancelAfter(timeoutMs);

                        var retryResult = await freshClient.CallToolAsync(
                            request.ToolName, arguments, cancellationToken: retryCts.Token);

                        if (rewriteResponse)
                        {
                            var freshGateway = GetOrCreateAttachmentGateway(serverName);
                            if (freshGateway is not null)
                            {
                                retryResult = await freshGateway.RewriteResponseAsync(
                                    request.ToolName, arguments, retryResult, ct);
                            }
                        }

                        retryResult = await CaptureBinaryContentAsync(
                            serverName, request.ToolName, retryResult, ct);

                        sw.Stop();
                        var retryBlocks = McpToolExecutor.MapContentBlocks(retryResult);
                        var retryContent = retryBlocks is not null ? McpToolExecutor.TextFromBlocks(retryBlocks) : null;

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
                Message = ex.Message,
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

            var tools = _serverTools.GetValueOrDefault(req.ServerName) ?? [];
            var serverPrompts = _serverPrompts.GetValueOrDefault(req.ServerName) ?? [];
            var metadata = _serverMetadata.GetValueOrDefault(req.ServerName);
            var response = new McpGetServiceDetailsResponse
            {
                ServerName = req.ServerName,
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
                Prompts = serverPrompts.Select(p => new McpPromptDefinition
                {
                    Name = p.Name,
                    Description = p.Description,
                    Arguments = (p.ProtocolPrompt.Arguments ?? [])
                        .Select(a => new McpPromptArgument
                        {
                            Name = a.Name,
                            Description = a.Description,
                            Required = a.Required ?? false
                        }).ToList()
                }).ToList(),
                Error = _clients.ContainsKey(req.ServerName) ? null
                    : $"Server '{req.ServerName}' is not connected"
            };

            await PublishResponseAsync(response, replyTo, envelope.CorrelationId, ct);
        }
        else if (envelope.MessageType == typeof(McpRegisterServerRequest).FullName)
        {
            var req = envelope.GetPayload<McpRegisterServerRequest>();
            if (req is null) return MessageResult.DeadLetter;

            try
            {
                var config = new McpBridgeServerConfig
                {
                    Type = req.Type,
                    Url = req.Url,
                    Command = req.Command,
                    Args = req.Args,
                    Env = req.Env
                };

                // register_mcp_server cannot express argGuards, and it is LLM-callable —
                // re-registering an existing name must not strip operator-declared policy.
                if (_serverConfigs.TryGetValue(req.ServerName, out var existingConfig))
                    config.ArgGuards = existingConfig.ArgGuards;

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
                    var existingDup = _serverConfigs.FirstOrDefault(kvp =>
                        !string.Equals(kvp.Key, req.ServerName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(kvp.Value.CanonicalIdentity(), newIdentity, StringComparison.Ordinal));

                    if (existingDup.Key is not null)
                    {
                        var dupResponse = new McpRegisterServerResponse
                        {
                            ServerName = req.ServerName,
                            Success = false,
                            Error = $"An MCP server with the same URL and credentials is already registered as '{existingDup.Key}'. " +
                                    $"Use the existing registration, or unregister it before registering under a different name."
                        };
                        await PublishResponseAsync(dupResponse, replyTo, envelope.CorrelationId, ct);
                        return MessageResult.Ack;
                    }
                }

                await ConnectServerAsync(req.ServerName, config, ct);
                await PersistServerConfigAsync(req.ServerName, config, remove: false);

                var summary = _serverTools.ContainsKey(req.ServerName)
                    ? $"{_serverTools[req.ServerName].Count} tool(s) available."
                    : null;

                var response = new McpRegisterServerResponse
                {
                    ServerName = req.ServerName,
                    Success = _clients.ContainsKey(req.ServerName),
                    Summary = summary,
                    Error = _clients.ContainsKey(req.ServerName) ? null : "Connection failed"
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

            var client = _clients.GetValueOrDefault(req.ServerName);
            if (client is null)
            {
                var notFound = new McpGetPromptResponse
                {
                    ServerName = req.ServerName,
                    PromptName = req.PromptName,
                    Error = $"Server '{req.ServerName}' is not connected"
                };
                await PublishResponseAsync(notFound, replyTo, envelope.CorrelationId, ct);
                return MessageResult.Ack;
            }

            try
            {
                IReadOnlyDictionary<string, object?> promptArgs = req.Arguments.Count > 0
                    ? req.Arguments.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value)
                    : new Dictionary<string, object?>();

                var result = await client.GetPromptAsync(req.PromptName, promptArgs, cancellationToken: ct);

                var messages = (result.Messages ?? []).Select(m =>
                {
                    string content;
                    string contentType;
                    if (m.Content is ModelContextProtocol.Protocol.TextContentBlock textBlock)
                    {
                        content = textBlock.Text;
                        contentType = "text";
                    }
                    else
                    {
                        content = JsonSerializer.Serialize(m.Content, JsonOptions);
                        contentType = m.Content?.Type ?? "unknown";
                    }
                    return new McpPromptMessage
                    {
                        Role = m.Role.ToString().ToLowerInvariant(),
                        Content = content,
                        ContentType = contentType
                    };
                }).ToList();

                var response = new McpGetPromptResponse
                {
                    ServerName = req.ServerName,
                    PromptName = req.PromptName,
                    Description = result.Description,
                    Messages = messages
                };
                await PublishResponseAsync(response, replyTo, envelope.CorrelationId, ct);
            }
            catch (Exception ex)
            {
                // Attempt reconnect and retry once (same pattern as HandleToolInvokeAsync)
                if (_serverConfigs.TryGetValue(req.ServerName, out var staleConfig))
                {
                    _logger.LogWarning(ex,
                        "GetPrompt {Server}/{Prompt} FAILED — reconnecting and retrying transparently",
                        req.ServerName, req.PromptName);
                    try
                    {
                        await ConnectServerAsync(req.ServerName, staleConfig, ct);
                        var freshClient = _clients.GetValueOrDefault(req.ServerName);
                        if (freshClient is not null)
                        {
                            IReadOnlyDictionary<string, object?> retryArgs = req.Arguments.Count > 0
                                ? req.Arguments.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value)
                                : new Dictionary<string, object?>();

                            var retryResult = await freshClient.GetPromptAsync(req.PromptName, retryArgs, cancellationToken: ct);
                            var retryMessages = (retryResult.Messages ?? []).Select(m =>
                            {
                                string content;
                                string contentType;
                                if (m.Content is ModelContextProtocol.Protocol.TextContentBlock textBlock)
                                {
                                    content = textBlock.Text;
                                    contentType = "text";
                                }
                                else
                                {
                                    content = JsonSerializer.Serialize(m.Content, JsonOptions);
                                    contentType = m.Content?.Type ?? "unknown";
                                }
                                return new McpPromptMessage
                                {
                                    Role = m.Role.ToString().ToLowerInvariant(),
                                    Content = content,
                                    ContentType = contentType
                                };
                            }).ToList();

                            var retryResponse = new McpGetPromptResponse
                            {
                                ServerName = req.ServerName,
                                PromptName = req.PromptName,
                                Description = retryResult.Description,
                                Messages = retryMessages
                            };
                            await PublishResponseAsync(retryResponse, replyTo, envelope.CorrelationId, ct);
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
            if (_serverConfigs.TryGetValue(request.ServerName, out var config))
            {
                // Reconnect rather than refresh the stale client — this handles server restarts.
                await ConnectServerAsync(request.ServerName, config, ct);
            }
        }
        else
        {
            foreach (var (name, _) in _clients.ToList())
            {
                if (!_serverConfigs.TryGetValue(name, out var config)) continue;

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
                var disconnected = _serverConfigs.Keys
                    .Where(name => !_clients.ContainsKey(name))
                    .ToList();

                foreach (var name in disconnected)
                {
                    if (!_serverConfigs.TryGetValue(name, out var config)) continue;

                    _logger.LogInformation(
                        "Reconnect sweep: attempting to reconnect MCP server {Name}", name);
                    try
                    {
                        await ConnectServerAsync(name, config, ct);
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
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
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

    private async Task DisposeClientsAsync()
    {
        foreach (var (_, client) in _clients)
        {
            try { await client.DisposeAsync(); }
            catch { /* Best-effort cleanup */ }
        }
        _clients.Clear();
        _serverTools.Clear();
        _serverPrompts.Clear();
        _serverMetadata.Clear();
        _serverConfigs.Clear();
        _serverSummaries.Clear();

        foreach (var (_, entry) in _attachmentGateways)
        {
            try { entry.HttpClient.Dispose(); }
            catch { /* Best-effort cleanup */ }
        }
        _attachmentGateways.Clear();
    }

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
        // Snapshot the affected servers under the same lock domain we use elsewhere.
        // Modifications to _serverConfigs come from foreground async paths; reading
        // the keys here is safe because we tolerate races (a server added/removed
        // mid-flip just gets the next publish cycle).
        var workiqServers = _serverConfigs
            .Where(kvp => string.Equals(kvp.Value.Auth?.Profile, "workiq",
                StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
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
                        if (_serverSummaries.TryGetValue(name, out var summary))
                        {
                            await PublishServersIndexedAsync([summary], [], CancellationToken.None);
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
