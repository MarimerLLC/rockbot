using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.Tools.Mcp;

namespace RockBot.Cli.McpBridge;

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

    private readonly Dictionary<string, McpClient> _clients = [];
    private readonly Dictionary<string, McpBridgeServerConfig> _serverConfigs = [];
    private readonly Dictionary<string, List<McpClientTool>> _serverTools = [];
    private ISubscription? _invokeSubscription;
    private ISubscription? _refreshSubscription;
    private ISubscription? _manageSubscription;
    private FileSystemWatcher? _configWatcher;

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
        ILlmClient? llmClient = null)
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

        // Load config and connect to servers
        await LoadConfigAndConnectAsync(cancellationToken);
        _startupCompletedAt = DateTimeOffset.UtcNow;

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
        if (_manageSubscription is not null)
            await _manageSubscription.DisposeAsync();

        await DisposeClientsAsync();
    }

    private async Task LoadConfigAndConnectAsync(CancellationToken ct)
    {
        if (!File.Exists(_configPath))
        {
            _logger.LogWarning("MCP config file not found at {Path}", _configPath);
            return;
        }

        McpBridgeConfig config;
        try
        {
            var json = await File.ReadAllTextAsync(_configPath, ct);
            config = JsonSerializer.Deserialize<McpBridgeConfig>(json, JsonOptions)
                ?? new McpBridgeConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read MCP config from {Path}", _configPath);
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

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url)
            });
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

            _clients[name] = client;
            _serverConfigs[name] = config;

            // Discover tools
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            var filteredTools = ApplyToolFilters(tools.ToList(), config);
            _serverTools[name] = filteredTools;

            _logger.LogInformation("Connected to MCP server {Name} with {Count} tools",
                name, filteredTools.Count);

            // Build and publish server summary
            var summary = await GenerateSummaryAsync(name, filteredTools, ct);
            await PublishServersIndexedAsync([summary], [], ct);
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

        _serverTools.Remove(name);
        _serverConfigs.Remove(name);

        await PublishServersIndexedAsync([], [name], CancellationToken.None);

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

    private async Task<McpServerSummary> GenerateSummaryAsync(
        string serverName,
        List<McpClientTool> tools,
        CancellationToken ct)
    {
        var toolNames = tools.Select(t => t.Name).ToList();

        string? summaryText = null;

        if (_options.GenerateLlmSummaries && _llmClient is not null && tools.Count > 0)
        {
            try
            {
                var toolList = string.Join("\n", tools.Take(20).Select(t =>
                    $"- {t.Name}: {t.Description}"));

                var prompt = $"""
                    Write a single brief sentence (15-25 words) describing what the '{serverName}' MCP server provides.
                    Based on these tools:
                    {toolList}
                    Respond with only the sentence, no preamble or explanation.
                    """;

                var messages = new[] { new ChatMessage(ChatRole.User, prompt) };
                var response = await _llmClient.GetResponseAsync(messages, cancellationToken: ct);
                summaryText = response.Text?.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate LLM summary for {ServerName}, using fallback", serverName);
            }
        }

        summaryText ??= tools.Count > 0
            ? $"Provides {tools.Count} tool(s): {string.Join(", ", toolNames.Take(10))}" +
              (toolNames.Count > 10 ? $" and {toolNames.Count - 10} more." : ".")
            : "No tools available.";

        return new McpServerSummary
        {
            ServerName = serverName,
            Summary = summaryText,
            ToolCount = tools.Count,
            ToolNames = toolNames
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

            var result = await client.CallToolAsync(
                request.ToolName, arguments, cancellationToken: timeoutCts.Token);

            sw.Stop();
            var content = McpToolExecutor.FormatResult(result);

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
            var response = new McpGetServiceDetailsResponse
            {
                ServerName = req.ServerName,
                Tools = tools.Select(t => new McpToolDefinition
                {
                    Name = t.Name,
                    Description = t.Description ?? string.Empty,
                    ParametersSchema = t.JsonSchema.ValueKind != JsonValueKind.Undefined
                        ? t.JsonSchema.GetRawText()
                        : null
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
        else
        {
            _logger.LogWarning("Unknown management message type: {MessageType}", envelope.MessageType);
        }

        return MessageResult.Ack;
    }

    private async Task PersistServerConfigAsync(string name, McpBridgeServerConfig? config, bool remove)
    {
        try
        {
            McpBridgeConfig current;
            if (File.Exists(_configPath))
            {
                var json = await File.ReadAllTextAsync(_configPath);
                current = JsonSerializer.Deserialize<McpBridgeConfig>(json, JsonOptions)
                    ?? new McpBridgeConfig();
            }
            else
            {
                current = new McpBridgeConfig();
            }

            if (remove)
                current.McpServers.Remove(name);
            else if (config is not null)
                current.McpServers[name] = config;

            var updated = JsonSerializer.Serialize(current, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            await File.WriteAllTextAsync(_configPath, updated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist server config change for {ServerName}", name);
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
            if (_clients.TryGetValue(request.ServerName, out var client)
                && _serverConfigs.TryGetValue(request.ServerName, out var config))
            {
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                var filtered = ApplyToolFilters(tools.ToList(), config);
                _serverTools[request.ServerName] = filtered;

                var summary = await GenerateSummaryAsync(request.ServerName, filtered, ct);
                await PublishServersIndexedAsync([summary], [], ct);
            }
        }
        else
        {
            var summaries = new List<McpServerSummary>();
            foreach (var (name, client) in _clients)
            {
                if (!_serverConfigs.TryGetValue(name, out var config)) continue;

                var tools = await client.ListToolsAsync(cancellationToken: ct);
                var filtered = ApplyToolFilters(tools.ToList(), config);
                _serverTools[name] = filtered;

                summaries.Add(await GenerateSummaryAsync(name, filtered, ct));
            }

            if (summaries.Count > 0)
                await PublishServersIndexedAsync(summaries, [], ct);
        }

        return MessageResult.Ack;
    }

    private void SetupConfigWatcher()
    {
        var directory = Path.GetDirectoryName(_configPath);

        if (directory is null) return;

        var fileName = Path.GetFileName(_configPath);
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
        await DisposeClientsAsync();

        if (_invokeSubscription is not null)
            await _invokeSubscription.DisposeAsync();
        if (_refreshSubscription is not null)
            await _refreshSubscription.DisposeAsync();
        if (_manageSubscription is not null)
            await _manageSubscription.DisposeAsync();
    }
}
