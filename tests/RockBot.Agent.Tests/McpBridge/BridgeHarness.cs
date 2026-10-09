using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RockBot.Agent.McpBridge;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.Tools.Mcp;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// Drives a real <see cref="McpBridgeService"/> against a real MCP server running in-process over
/// streamable HTTP. The message bus is replaced by stubs that hand each request straight to the
/// bridge's subscription handler and capture what it publishes back, so a test exercises the whole
/// invoke path — routing, pre-checks, the SDK client, the downstream, and error shaping — without
/// RabbitMQ.
/// </summary>
internal sealed class BridgeHarness : IAsyncDisposable
{
    public const string ServerName = "fixture";

    /// <summary><c>serverInfo.version</c> the fixture server reports.</summary>
    public const string HarnessServerVersion = "1.2.3";
    private const string AgentName = "test-agent";

    private readonly WebApplication _server;
    private readonly IOptions<McpBridgeOptions> _options;
    private readonly ILlmClient? _llmClient;
    private readonly string _configDir;
    private IConversationMemory? _conversationMemory;
    private IWorkingMemory? _workingMemory;
    private TimeProvider? _timeProvider;
    private McpBridgeService _bridge = null!;
    private TopicSubscriber _subscriber = null!;
    private CapturingPublisher _publisher = null!;

    private BridgeHarness(
        WebApplication server, IOptions<McpBridgeOptions> options, ILlmClient? llmClient, string configDir,
        List<McpServerTool> lateTools, List<McpServerResource> lateResources)
    {
        _server = server;
        _lateTools = lateTools;
        _lateResources = lateResources;
        _options = options;
        _llmClient = llmClient;
        _configDir = configDir;
    }

    /// <summary>The bridge under test.</summary>
    public McpBridgeService Bridge => _bridge;

    /// <summary>
    /// The in-memory bus, for wiring agent-side components (proxy, management executor) to the
    /// bridge: a publish is captured and delivered to whatever subscribed to that exact topic.
    /// </summary>
    public IMessagePublisher BusPublisher => _publisher;

    /// <inheritdoc cref="BusPublisher"/>
    public IMessageSubscriber BusSubscriber => _subscriber;

    /// <summary>Path of the bridge's <c>mcp.json</c>.</summary>
    public string ConfigPath => _options.Value.ConfigPath;

    /// <summary>The fixture server's MCP endpoint.</summary>
    public string ServerUrl { get; private init; } = "";

    /// <summary>Every <see cref="McpServersIndexed"/> the bridge has published since it (re)started.</summary>
    public IReadOnlyList<McpServersIndexed> IndexMessages => _publisher.Published
        .Where(p => p.Envelope.MessageType == typeof(McpServersIndexed).FullName)
        .Select(p => p.Envelope.GetPayload<McpServersIndexed>()!)
        .ToList();

    /// <summary>Adds a tool to the running fixture server, as an upgraded server would.</summary>
    public void AddServerTool(McpServerTool tool)
    {
        // Stateless HTTP may build server options per request (from the Configure callback in
        // StartAsync) or once (the cached options instance); covering both keeps this independent
        // of which the SDK does.
        lock (_lateTools) _lateTools.Add(tool);
        _server.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection?.Add(tool);
    }

    private readonly List<McpServerTool> _lateTools;

    /// <summary>
    /// Adds a resource to the running fixture server. The server must have started with
    /// resources (possibly an empty list), or it doesn't advertise the capability at all.
    /// </summary>
    public void AddServerResource(McpServerResource resource)
    {
        lock (_lateResources) _lateResources.Add(resource);
        _server.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ResourceCollection?.Add(resource);
    }

    private readonly List<McpServerResource> _lateResources;

    /// <summary>Where the bridge saves attachments and captured content.</summary>
    public string AttachmentsPath => Path.Combine(_configDir, "attachments");

    /// <summary>Everything the bridge has logged, with its level.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> BridgeLog => [.. _bridgeLog.Entries];

    private readonly CapturingLogger _bridgeLog = new();

    /// <summary>Stops the bridge and starts a fresh one on the same <c>mcp.json</c>.</summary>
    public async Task RestartBridgeAsync()
    {
        await _bridge.StopAsync(CancellationToken.None);
        await _bridge.DisposeAsync();
        await StartBridgeAsync();
    }

    private async Task StartBridgeAsync()
    {
        _subscriber = new TopicSubscriber();
        _publisher = new CapturingPublisher(_subscriber);
        // Attachment storage defaults to the shared volume (/rockbot/shared), which a test runner
        // can't write; keep it inside the run's temporary directory.
        _bridge = new McpBridgeService(
            _publisher, _subscriber, new AgentIdentity(AgentName), _options, _bridgeLog,
            llmClient: _llmClient, tokenProviders: null, healthTracker: null, argGuards: null,
            elicitationResponder: null, services: null,
            attachmentStorage: new AttachmentStorage(AttachmentsPath),
            conversationMemory: _conversationMemory, workingMemory: _workingMemory, timeProvider: _timeProvider);
        await _bridge.StartAsync(CancellationToken.None);
    }

    /// <summary>
    /// Starts the fixture server with <paramref name="tools"/>, <paramref name="prompts"/> and
    /// <paramref name="resources"/>, then starts the bridge with one configured server pointing at
    /// it. <paramref name="configure"/> may adjust the server entry (tool filters, guards, …) before
    /// the bridge reads it. A null <paramref name="resources"/> leaves the server without the
    /// resources capability; an empty one advertises it with nothing in it.
    /// </summary>
    public static async Task<BridgeHarness> StartAsync(
        IEnumerable<McpServerTool> tools,
        IEnumerable<McpServerPrompt>? prompts = null,
        Action<McpBridgeServerConfig>? configure = null,
        ILlmClient? llmClient = null,
        IEnumerable<McpServerResource>? resources = null,
        Action<McpBridgeOptions>? configureBridge = null,
        IConversationMemory? conversationMemory = null,
        IWorkingMemory? workingMemory = null,
        Action<string>? beforeBridgeStarts = null,
        TimeProvider? timeProvider = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var lateTools = new List<McpServerTool>();
        var lateResources = new List<McpServerResource>();
        var mcp = builder.Services.AddMcpServer().WithHttpTransport().WithTools(tools);
        if (prompts is not null)
            mcp.WithPrompts(prompts);
        if (resources is not null)
            mcp.WithResources(resources);
        builder.Services.Configure<McpServerOptions>(o =>
        {
            o.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "harness", Version = HarnessServerVersion };
            lock (lateTools)
            {
                foreach (var tool in lateTools)
                {
                    o.ToolCollection ??= [];
                    if (!o.ToolCollection.Contains(tool)) o.ToolCollection.Add(tool);
                }
            }
            lock (lateResources)
            {
                foreach (var resource in lateResources)
                {
                    o.ResourceCollection ??= [];
                    if (!o.ResourceCollection.Contains(resource)) o.ResourceCollection.Add(resource);
                }
            }
        });

        var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync();

        var address = server.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var entry = new McpBridgeServerConfig { Type = "http", Url = $"{address}/mcp" };
        configure?.Invoke(entry);

        var configDir = Path.Combine(Path.GetTempPath(), "rockbot-bridge-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "mcp.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(
            new McpBridgeConfig { McpServers = { [ServerName] = entry } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var bridgeOptions = new McpBridgeOptions
        {
            ConfigPath = configPath,
            GenerateLlmSummaries = llmClient is not null,
            ConnectRetryCount = 0,
            ReconnectSweepIntervalSeconds = 0,
            ConfigPollIntervalSeconds = 0,
        };
        configureBridge?.Invoke(bridgeOptions);
        var options = Options.Create(bridgeOptions);

        var harness = new BridgeHarness(server, options, llmClient, configDir, lateTools, lateResources)
        {
            ServerUrl = entry.Url,
            _conversationMemory = conversationMemory,
            _workingMemory = workingMemory,
            _timeProvider = timeProvider,
        };

        // Lets a test seed files (the pending-question ledger) the bridge reads as it starts.
        beforeBridgeStarts?.Invoke(configDir);
        await harness.StartBridgeAsync();
        return harness;
    }

    /// <summary>Sends an <c>mcp_register_server</c> management request.</summary>
    public async Task<McpRegisterServerResponse> RegisterAsync(string name, string url)
    {
        var request = new McpRegisterServerRequest { ServerName = name, Type = "http", Url = url };
        var reply = await SendAsync(McpManagementExecutor.ManageTopic, request, headers: null);
        return reply.GetPayload<McpRegisterServerResponse>()!;
    }

    /// <summary>Sends an <c>mcp_unregister_server</c> management request.</summary>
    public async Task<McpUnregisterServerResponse> UnregisterAsync(string name)
    {
        var request = new McpUnregisterServerRequest { ServerName = name };
        var reply = await SendAsync(McpManagementExecutor.ManageTopic, request, headers: null);
        return reply.GetPayload<McpUnregisterServerResponse>()!;
    }

    /// <summary>The server entries as the bridge last persisted them to <c>mcp.json</c>.</summary>
    public McpBridgeConfig ReadPersistedConfig() =>
        JsonSerializer.Deserialize<McpBridgeConfig>(File.ReadAllText(ConfigPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    /// <summary>Rewrites one entry of <c>mcp.json</c>, as an operator would by hand.</summary>
    public void EditPersistedEntry(string name, Action<McpBridgeServerConfig> edit)
    {
        var config = ReadPersistedConfig();
        edit(config.McpServers[name]);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    /// <summary>
    /// Sends a tool invoke routed to <paramref name="server"/> exactly as <c>mcp_invoke_tool</c>
    /// does, and returns what the agent side would receive (a <see cref="ToolError"/> is mapped to
    /// an error response, as <see cref="McpToolProxy"/> maps it).
    /// </summary>
    public async Task<ToolInvokeResponse> InvokeAsync(
        string tool, string? argumentsJson, string server = ServerName,
        string sessionId = "session-1", bool canAnswer = false)
    {
        var request = new ToolInvokeRequest
        {
            ToolCallId = Guid.NewGuid().ToString("N"),
            ToolName = tool,
            Arguments = argumentsJson,
            SessionId = sessionId,
        };

        var headers = new Dictionary<string, string> { [McpHeaders.ServerName] = server };
        if (canAnswer)
            headers[McpHeaders.Handback] = McpHeaders.HandbackCapable;

        var reply = await SendAsync(McpToolProxy.InvokeTopic, request, headers);

        if (reply.MessageType == typeof(ToolError).FullName)
        {
            var error = reply.GetPayload<ToolError>()!;
            return new ToolInvokeResponse
            {
                ToolCallId = error.ToolCallId,
                ToolName = error.ToolName,
                Content = error.Message,
                IsError = true,
            };
        }

        return reply.GetPayload<ToolInvokeResponse>()!;
    }

    /// <summary>Sends an <c>mcp_get_prompt</c> management request.</summary>
    public async Task<McpGetPromptResponse> GetPromptAsync(
        string prompt, Dictionary<string, string>? arguments = null, string server = ServerName)
    {
        var request = new McpGetPromptRequest
        {
            ServerName = server,
            PromptName = prompt,
            Arguments = arguments ?? [],
        };

        var reply = await SendAsync(McpManagementExecutor.ManageTopic, request, headers: null);
        return reply.GetPayload<McpGetPromptResponse>()!;
    }

    /// <summary>Sends an <c>mcp_list_resources</c> management request.</summary>
    public async Task<McpListResourcesResponse> ListResourcesAsync(string server = ServerName)
    {
        var reply = await SendAsync(McpManagementExecutor.ManageTopic,
            new McpListResourcesRequest { ServerName = server }, headers: null);
        return reply.GetPayload<McpListResourcesResponse>()!;
    }

    /// <summary>Sends an <c>mcp_read_resource</c> management request.</summary>
    public async Task<McpReadResourceResponse> ReadResourceAsync(string uri, string server = ServerName)
    {
        var reply = await SendAsync(McpManagementExecutor.ManageTopic,
            new McpReadResourceRequest { ServerName = server, Uri = uri }, headers: null);
        return reply.GetPayload<McpReadResourceResponse>()!;
    }

    /// <summary>Where the bridge keeps its pending-question ledger in this run.</summary>
    public string LedgerPath => Path.Combine(_configDir, "mcp", "pending-questions.json");

    /// <summary>Everything the bridge published on <paramref name="topic"/>.</summary>
    public IReadOnlyList<MessageEnvelope> PublishedOn(string topic) =>
        [.. _publisher.Published.Where(p => p.Topic == topic).Select(p => p.Envelope)];

    /// <summary>
    /// Sends an <c>mcp_answer</c> request and waits for its reply. An accepted answer is replied to
    /// by the parked call once the resumed call completes, after the handler has returned.
    /// </summary>
    public async Task<McpAnswerQuestionResponse> AnswerAsync(
        string questionId, string? answersJson, bool decline = false, string sessionId = "session/test",
        TimeSpan? wait = null)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var replyTo = $"reply.{correlationId}";
        var envelope = new McpAnswerQuestionRequest
        {
            QuestionId = questionId,
            SessionId = sessionId,
            ToolCallId = "answer-call",
            Answers = answersJson,
            Decline = decline,
        }.ToEnvelope(source: AgentName, correlationId: correlationId, replyTo: replyTo);

        await _subscriber.DeliverAsync(McpManagementExecutor.ManageTopic, envelope);

        var reply = await WaitForAsync(replyTo, wait ?? TimeSpan.FromSeconds(15));
        return reply.GetPayload<McpAnswerQuestionResponse>()!;
    }

    /// <summary>Waits for the first message the bridge publishes on <paramref name="topic"/>.</summary>
    public async Task<MessageEnvelope> WaitForAsync(string topic, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var published = _publisher.Published.FirstOrDefault(p => p.Topic == topic);
            if (published.Envelope is not null)
                return published.Envelope;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"The bridge published nothing on {topic} within {timeout}.");
            await Task.Delay(25);
        }
    }

    private async Task<MessageEnvelope> SendAsync<T>(string topic, T payload, IReadOnlyDictionary<string, string>? headers)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var replyTo = $"reply.{correlationId}";
        var envelope = payload.ToEnvelope(
            source: AgentName, correlationId: correlationId, replyTo: replyTo, headers: headers);

        await _subscriber.DeliverAsync(topic, envelope);

        var published = _publisher.Published.LastOrDefault(p => p.Topic == replyTo);
        Assert.IsNotNull(published.Envelope, $"The bridge published no reply on {replyTo}.");
        return published.Envelope;
    }

    public async ValueTask DisposeAsync()
    {
        await _bridge.StopAsync(CancellationToken.None);
        await _bridge.DisposeAsync();
        await _server.StopAsync();
        await _server.DisposeAsync();
        try { Directory.Delete(_configDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Records every bridge log entry, so a test can assert what was (not) logged.</summary>
    private sealed class CapturingLogger : ILogger<McpBridgeService>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>Keeps every subscription's handler, keyed by topic.</summary>
    private sealed class TopicSubscriber : IMessageSubscriber
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<MessageEnvelope, CancellationToken, Task<MessageResult>>> _handlers = new();

        public Task<ISubscription> SubscribeAsync(
            string topic,
            string subscriptionName,
            Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
            CancellationToken cancellationToken = default,
            int dispatchConcurrency = 1)
        {
            _handlers[topic] = handler;
            return Task.FromResult<ISubscription>(new NoopSubscription());
        }

        public Task DeliverAsync(string topic, MessageEnvelope envelope)
        {
            Assert.IsTrue(_handlers.TryGetValue(topic, out var handler), $"The bridge has no subscription on {topic}.");
            return handler(envelope, CancellationToken.None);
        }

        public Task TryDeliverAsync(string topic, MessageEnvelope envelope) =>
            _handlers.TryGetValue(topic, out var handler) ? handler(envelope, CancellationToken.None) : Task.CompletedTask;

        public ValueTask DisposeAsync() => default;

        private sealed class NoopSubscription : ISubscription
        {
            public string Topic => string.Empty;
            public string SubscriptionName => string.Empty;
            public bool IsActive => true;
            public ValueTask DisposeAsync() => default;
        }
    }

    private sealed class CapturingPublisher(TopicSubscriber subscriber) : IMessagePublisher
    {
        private readonly object _gate = new();
        private readonly List<(string Topic, MessageEnvelope Envelope)> _published = [];

        public IReadOnlyList<(string Topic, MessageEnvelope Envelope)> Published
        {
            get { lock (_gate) return [.. _published]; }
        }

        public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            lock (_gate) _published.Add((topic, envelope));
            return subscriber.TryDeliverAsync(topic, envelope);
        }

        public ValueTask DisposeAsync() => default;
    }
}
