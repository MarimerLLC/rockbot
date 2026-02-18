using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RockBot.Messaging;
using RockBot.Tools;

namespace RockBot.Tools.Mcp.Bridge;

/// <summary>
/// Hosted service that manages MCP server connections, handles tool invoke requests
/// from the message bus, and publishes tool discovery/response messages.
/// </summary>
public sealed class McpBridgeService : IHostedService, IAsyncDisposable
{
    private readonly IMessagePublisher _publisher;
    private readonly IMessageSubscriber _subscriber;
    private readonly McpBridgeOptions _options;
    private readonly ILogger<McpBridgeService> _logger;

    private readonly Dictionary<string, McpClient> _clients = [];
    private readonly Dictionary<string, McpBridgeServerConfig> _serverConfigs = [];
    private readonly Dictionary<string, List<McpClientTool>> _serverTools = [];
    private ISubscription? _invokeSubscription;
    private ISubscription? _refreshSubscription;
    private FileSystemWatcher? _configWatcher;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public McpBridgeService(
        IMessagePublisher publisher,
        IMessageSubscriber subscriber,
        IOptions<McpBridgeOptions> options,
        ILogger<McpBridgeService> logger)
    {
        _publisher = publisher;
        _subscriber = subscriber;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe to tool invoke requests
        _invokeSubscription = await _subscriber.SubscribeAsync(
            McpToolProxy.InvokeTopic,
            $"mcp-bridge.{_options.AgentName}",
            HandleToolInvokeAsync,
            cancellationToken);

        // Subscribe to metadata refresh requests
        _refreshSubscription = await _subscriber.SubscribeAsync(
            "tool.meta.mcp.refresh",
            $"mcp-bridge.{_options.AgentName}.refresh",
            HandleRefreshRequestAsync,
            cancellationToken);

        // Load config and connect to servers
        await LoadConfigAndConnectAsync(cancellationToken);

        // Watch for config changes
        SetupConfigWatcher();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _configWatcher?.Dispose();
        _configWatcher = null;

        if (_invokeSubscription is not null)
            await _invokeSubscription.DisposeAsync();
        if (_refreshSubscription is not null)
            await _refreshSubscription.DisposeAsync();

        await DisposeClientsAsync();
    }

    private async Task LoadConfigAndConnectAsync(CancellationToken ct)
    {
        if (!File.Exists(_options.ConfigPath))
        {
            _logger.LogWarning("MCP config file not found at {Path}", _options.ConfigPath);
            return;
        }

        McpBridgeConfig config;
        try
        {
            var json = await File.ReadAllTextAsync(_options.ConfigPath, ct);
            config = JsonSerializer.Deserialize<McpBridgeConfig>(json, JsonOptions)
                ?? new McpBridgeConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read MCP config from {Path}", _options.ConfigPath);
            return;
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
    }

    private async Task ConnectServerAsync(string name, McpBridgeServerConfig config, CancellationToken ct)
    {
        // Disconnect existing connection if any
        if (_clients.ContainsKey(name))
        {
            await DisconnectServerAsync(name);
        }

        try
        {
            McpClient client;

            if (config.IsSse)
            {
                if (string.IsNullOrEmpty(config.Url))
                {
                    _logger.LogError("SSE server {Name} missing URL", name);
                    return;
                }

                var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(config.Url)
                });
                client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            }
            else
            {
                if (string.IsNullOrEmpty(config.Command))
                {
                    _logger.LogError("Stdio server {Name} missing command", name);
                    return;
                }

                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = config.Command,
                    Arguments = config.Args,
                    EnvironmentVariables = config.Env!
                });
                client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            }

            _clients[name] = client;
            _serverConfigs[name] = config;

            // Discover tools
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            var filteredTools = ApplyToolFilters(tools.ToList(), config);
            _serverTools[name] = filteredTools;

            _logger.LogInformation("Connected to MCP server {Name} with {Count} tools",
                name, filteredTools.Count);

            // Publish tool availability
            await PublishToolsAvailableAsync(name, filteredTools, [], ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to MCP server {Name}", name);
        }
    }

    private async Task DisconnectServerAsync(string name)
    {
        if (_clients.Remove(name, out var client))
        {
            try { await client.DisposeAsync(); }
            catch { /* Best-effort cleanup */ }
        }

        var removedTools = new List<string>();
        if (_serverTools.Remove(name, out var tools))
        {
            removedTools.AddRange(tools.Select(t => t.Name));
        }
        _serverConfigs.Remove(name);

        if (removedTools.Count > 0)
        {
            await PublishToolsAvailableAsync(name, [], removedTools, CancellationToken.None);
        }

        _logger.LogInformation("Disconnected from MCP server {Name}", name);
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

    private async Task PublishToolsAvailableAsync(
        string serverName,
        List<McpClientTool> tools,
        List<string> removedTools,
        CancellationToken ct)
    {
        var message = new McpToolsAvailable
        {
            ServerName = serverName,
            Tools = tools.Select(t => new McpToolDefinition
            {
                Name = t.Name,
                Description = t.Description ?? string.Empty,
                ParametersSchema = t.JsonSchema.ValueKind != JsonValueKind.Undefined
                    ? t.JsonSchema.GetRawText()
                    : null
            }).ToList(),
            RemovedTools = removedTools
        };

        var topic = $"tool.meta.mcp.{_options.AgentName}";
        var envelope = message.ToEnvelope(
            source: $"mcp-bridge.{_options.AgentName}",
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

        var replyTo = envelope.ReplyTo ?? $"tool.result.{_options.AgentName}";

        // Find which server has this tool
        string? serverName = null;
        McpClient? client = null;

        foreach (var (name, tools) in _serverTools)
        {
            if (tools.Any(t => t.Name == request.ToolName))
            {
                serverName = name;
                client = _clients.GetValueOrDefault(name);
                break;
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

        // Parse timeout from headers
        var timeoutMs = _options.DefaultTimeoutMs;
        if (envelope.Headers.TryGetValue(WellKnownHeaders.TimeoutMs, out var timeoutStr)
            && int.TryParse(timeoutStr, out var parsedTimeout))
        {
            timeoutMs = Math.Min(parsedTimeout, _options.DefaultTimeoutMs);
        }

        _logger.LogInformation("→ MCP {Server}/{Tool} args={Args}",
            serverName, request.ToolName, request.Arguments ?? "(none)");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            var arguments = McpToolExecutor.ParseArguments(request.Arguments);
            var result = await client.CallToolAsync(
                request.ToolName, arguments, cancellationToken: timeoutCts.Token);

            sw.Stop();
            var content = McpToolExecutor.FormatResult(result);

            if (result.IsError == true)
                _logger.LogWarning("← MCP {Server}/{Tool} ERROR in {ElapsedMs}ms: {Content}",
                    serverName, request.ToolName, sw.ElapsedMilliseconds, content);
            else
                _logger.LogInformation("← MCP {Server}/{Tool} OK in {ElapsedMs}ms ({ContentLen} chars)",
                    serverName, request.ToolName, sw.ElapsedMilliseconds, content?.Length ?? 0);

            var response = new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
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
                Message = $"MCP server '{serverName}' timed out after {timeoutMs}ms",
                IsRetryable = true
            };

            await PublishResponseAsync(error, replyTo, envelope.CorrelationId, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "← MCP {Server}/{Tool} FAILED after {ElapsedMs}ms",
                serverName, request.ToolName, sw.ElapsedMilliseconds);

            var error = new ToolError
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Code = ToolError.Codes.ExecutionFailed,
                Message = ex.Message,
                IsRetryable = false
            };

            await PublishResponseAsync(error, replyTo, envelope.CorrelationId, ct);
        }

        return MessageResult.Ack;
    }

    private async Task PublishResponseAsync<T>(
        T payload,
        string topic,
        string? correlationId,
        CancellationToken ct)
    {
        var envelope = payload.ToEnvelope(
            source: $"mcp-bridge.{_options.AgentName}",
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
        var request = envelope.GetPayload<McpMetadataRefreshRequest>();

        if (request?.ServerName is not null)
        {
            if (_clients.TryGetValue(request.ServerName, out var client)
                && _serverConfigs.TryGetValue(request.ServerName, out var config))
            {
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                var filtered = ApplyToolFilters(tools.ToList(), config);

                var removedTools = _serverTools.GetValueOrDefault(request.ServerName)?
                    .Select(t => t.Name)
                    .Except(filtered.Select(t => t.Name))
                    .ToList() ?? [];

                _serverTools[request.ServerName] = filtered;
                await PublishToolsAvailableAsync(request.ServerName, filtered, removedTools, ct);
            }
        }
        else
        {
            foreach (var (name, client) in _clients)
            {
                if (!_serverConfigs.TryGetValue(name, out var config)) continue;

                var tools = await client.ListToolsAsync(cancellationToken: ct);
                var filtered = ApplyToolFilters(tools.ToList(), config);

                var removedTools = _serverTools.GetValueOrDefault(name)?
                    .Select(t => t.Name)
                    .Except(filtered.Select(t => t.Name))
                    .ToList() ?? [];

                _serverTools[name] = filtered;
                await PublishToolsAvailableAsync(name, filtered, removedTools, ct);
            }
        }

        return MessageResult.Ack;
    }

    private void SetupConfigWatcher()
    {
        var configPath = Path.GetFullPath(_options.ConfigPath);
        var directory = Path.GetDirectoryName(configPath);
        var fileName = Path.GetFileName(configPath);

        if (directory is null) return;

        _configWatcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _configWatcher.Changed += OnConfigFileChanged;
        _configWatcher.Created += OnConfigFileChanged;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("MCP config file changed, reloading...");

        // Debounce — file watchers often fire multiple events
        Task.Delay(500).ContinueWith(async _ =>
        {
            try
            {
                await LoadConfigAndConnectAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reloading MCP config");
            }
        });
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
        _serverConfigs.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _configWatcher?.Dispose();
        await DisposeClientsAsync();

        if (_invokeSubscription is not null)
            await _invokeSubscription.DisposeAsync();
        if (_refreshSubscription is not null)
            await _refreshSubscription.DisposeAsync();
    }
}
