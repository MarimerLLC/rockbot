using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Agent.McpBridge;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.McpMeasure.Hosting;

/// <summary>
/// RockBot's MCP gateway in one process, in one wrapper mode: the real <see cref="McpBridgeService"/>
/// connected to the fixture servers, and the agent's side of it (proxy, management tools, typed
/// tool catalog and surface) fed the bridge's server index. The message bus is an in-memory stub
/// that hands each publish straight to the subscriber of that topic, as in the bridge's
/// end-to-end tests, so a tool call takes the production path without RabbitMQ.
/// </summary>
public sealed class MeasureRig : IAsyncDisposable
{
    private const string AgentName = "measure-agent";

    private readonly McpBridgeService _bridge;
    private readonly string _workDir;

    private MeasureRig(McpWrapperMode mode, McpBridgeService bridge, string workDir, OrderedToolRegistry registry,
        McpWrapperCatalog catalog, McpTypedToolSurface surface)
    {
        Mode = mode;
        _bridge = bridge;
        _workDir = workDir;
        Registry = registry;
        Catalog = catalog;
        Surface = surface;
    }

    public McpWrapperMode Mode { get; }

    /// <summary>The agent's tool registry: management tools, and typed tools in eager mode.</summary>
    public IToolRegistry Registry { get; }

    public McpWrapperCatalog Catalog { get; }

    public McpTypedToolSurface Surface { get; }

    public static async Task<MeasureRig> StartAsync(
        FixtureHost fixtures, McpWrapperMode mode, ILoggerFactory loggers, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "rockbot-mcp-measure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var configPath = Path.Combine(workDir, "mcp.json");
        var config = new McpBridgeConfig();
        foreach (var (name, url) in fixtures.Urls)
            config.McpServers[name] = new McpBridgeServerConfig { Type = "http", Url = url };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), ct);

        var bus = new StubBus();
        var identity = new AgentIdentity(AgentName);
        var bridge = new McpBridgeService(
            bus, bus, identity,
            Options.Create(new McpBridgeOptions
            {
                ConfigPath = configPath,
                GenerateLlmSummaries = false,
                ConnectRetryCount = 0,
                ReconnectSweepIntervalSeconds = 0,
                ConfigPollIntervalSeconds = 0,
            }),
            loggers.CreateLogger<McpBridgeService>(),
            llmClient: null, tokenProviders: null, healthTracker: null, argGuards: null,
            elicitationResponder: null, services: null,
            attachmentStorage: new AttachmentStorage(Path.Combine(workDir, "attachments")));
        await bridge.StartAsync(ct);

        // The agent's half, wired as McpServiceCollectionExtensions wires it in the agent.
        var options = Options.Create(new McpToolSurfaceOptions { WrapperMode = mode });
        var proxy = new McpToolProxy(bus, bus, identity, loggers.CreateLogger<McpToolProxy>(),
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        var index = new McpServerIndex();
        var surface = new McpTypedToolSurface(options, loggers.CreateLogger<McpTypedToolSurface>());
        var management = new McpManagementExecutor(index, proxy, bus, bus, identity,
            loggers.CreateLogger<McpManagementExecutor>(), TimeSpan.FromSeconds(60), typedTools: surface);
        var schemas = new ToolSchemaCache((server, token) => management.GetSchemasAsync(server, token));
        var registry = new OrderedToolRegistry();
        var catalog = new McpWrapperCatalog(registry, schemas, management, options,
            loggers.CreateLogger<McpWrapperCatalog>(), surface);
        var handler = new McpServersIndexedHandler(registry, index, management,
            loggers.CreateLogger<McpServersIndexedHandler>(), schemas, catalog);

        var context = new MessageHandlerContext
        {
            Envelope = new McpServersIndexed { Servers = [] }.ToEnvelope("bridge"),
            Agent = identity,
            Services = new ServiceCollection().BuildServiceProvider(),
            CancellationToken = ct
        };
        foreach (var message in bus.IndexMessages)
            await handler.HandleAsync(message, context);

        var connected = index.Servers.Select(s => s.ServerName).ToHashSet(StringComparer.Ordinal);
        var missing = fixtures.Urls.Keys.Where(n => !connected.Contains(n)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"The bridge did not index fixture server(s): {string.Join(", ", missing)}");

        return new MeasureRig(mode, bridge, workDir, registry, catalog, surface);
    }

    public async ValueTask DisposeAsync()
    {
        await _bridge.StopAsync(CancellationToken.None);
        await _bridge.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A registry that lists tools in registration order, as the agent's does.</summary>
    public sealed class OrderedToolRegistry : IToolRegistry
    {
        private readonly object _gate = new();
        private readonly List<(ToolRegistration Registration, IToolExecutor Executor)> _tools = [];

        public IReadOnlyList<ToolRegistration> GetTools()
        {
            lock (_gate) return [.. _tools.Select(t => t.Registration)];
        }

        public IToolExecutor? GetExecutor(string toolName)
        {
            lock (_gate) return _tools.FirstOrDefault(t => t.Registration.Name == toolName).Executor;
        }

        public void Register(ToolRegistration registration, IToolExecutor executor)
        {
            lock (_gate)
            {
                if (_tools.Any(t => t.Registration.Name == registration.Name))
                    throw new InvalidOperationException($"Tool '{registration.Name}' is already registered.");
                _tools.Add((registration, executor));
            }
        }

        public bool Unregister(string toolName)
        {
            lock (_gate) return _tools.RemoveAll(t => t.Registration.Name == toolName) > 0;
        }
    }

    /// <summary>
    /// Delivers each publish to the handler subscribed to exactly that topic, and keeps the
    /// bridge's server index messages for the agent side.
    /// </summary>
    private sealed class StubBus : IMessagePublisher, IMessageSubscriber
    {
        private readonly ConcurrentDictionary<string, Func<MessageEnvelope, CancellationToken, Task<MessageResult>>> _handlers = new();
        private readonly ConcurrentQueue<McpServersIndexed> _index = new();

        public IReadOnlyList<McpServersIndexed> IndexMessages => [.. _index];

        public Task<ISubscription> SubscribeAsync(
            string topic,
            string subscriptionName,
            Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
            CancellationToken cancellationToken = default,
            int dispatchConcurrency = 1)
        {
            _handlers[topic] = handler;
            return Task.FromResult<ISubscription>(new Subscription(topic, subscriptionName));
        }

        public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope.MessageType == typeof(McpServersIndexed).FullName
                && envelope.GetPayload<McpServersIndexed>() is { } indexed)
                _index.Enqueue(indexed);

            return _handlers.TryGetValue(topic, out var handler)
                ? handler(envelope, cancellationToken)
                : Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => default;

        private sealed class Subscription(string topic, string name) : ISubscription
        {
            public string Topic => topic;
            public string SubscriptionName => name;
            public bool IsActive => true;
            public ValueTask DisposeAsync() => default;
        }
    }
}
