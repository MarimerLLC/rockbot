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
    private const string AgentName = "test-agent";

    private readonly WebApplication _server;
    private readonly McpBridgeService _bridge;
    private readonly TopicSubscriber _subscriber;
    private readonly CapturingPublisher _publisher;
    private readonly string _configDir;

    private BridgeHarness(
        WebApplication server, McpBridgeService bridge, TopicSubscriber subscriber, CapturingPublisher publisher, string configDir)
    {
        _server = server;
        _bridge = bridge;
        _subscriber = subscriber;
        _publisher = publisher;
        _configDir = configDir;
    }

    /// <summary>
    /// Starts the fixture server with <paramref name="tools"/> and <paramref name="prompts"/>, then
    /// starts the bridge with one configured server pointing at it. <paramref name="configure"/>
    /// may adjust the server entry (tool filters, guards, …) before the bridge reads it.
    /// </summary>
    public static async Task<BridgeHarness> StartAsync(
        IEnumerable<McpServerTool> tools,
        IEnumerable<McpServerPrompt>? prompts = null,
        Action<McpBridgeServerConfig>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var mcp = builder.Services.AddMcpServer().WithHttpTransport().WithTools(tools);
        if (prompts is not null)
            mcp.WithPrompts(prompts);

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

        var options = Options.Create(new McpBridgeOptions
        {
            ConfigPath = configPath,
            GenerateLlmSummaries = false,
            ConnectRetryCount = 0,
            ReconnectSweepIntervalSeconds = 0,
            ConfigPollIntervalSeconds = 0,
        });

        var subscriber = new TopicSubscriber();
        var publisher = new CapturingPublisher();
        // Attachment storage defaults to the shared volume (/rockbot/shared), which a test runner
        // can't write; keep it inside the run's temporary directory.
        var bridge = new McpBridgeService(
            publisher, subscriber, new AgentIdentity(AgentName), options, NullLogger<McpBridgeService>.Instance,
            llmClient: null, tokenProviders: null, healthTracker: null, argGuards: null,
            elicitationResponder: null, services: null,
            attachmentStorage: new AttachmentStorage(Path.Combine(configDir, "attachments")));
        await bridge.StartAsync(CancellationToken.None);

        return new BridgeHarness(server, bridge, subscriber, publisher, configDir);
    }

    /// <summary>
    /// Sends a tool invoke routed to <paramref name="server"/> exactly as <c>mcp_invoke_tool</c>
    /// does, and returns what the agent side would receive (a <see cref="ToolError"/> is mapped to
    /// an error response, as <see cref="McpToolProxy"/> maps it).
    /// </summary>
    public async Task<ToolInvokeResponse> InvokeAsync(string tool, string? argumentsJson, string server = ServerName)
    {
        var request = new ToolInvokeRequest
        {
            ToolCallId = Guid.NewGuid().ToString("N"),
            ToolName = tool,
            Arguments = argumentsJson,
            SessionId = "session-1",
        };

        var reply = await SendAsync(McpToolProxy.InvokeTopic, request,
            new Dictionary<string, string> { [McpHeaders.ServerName] = server });

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

    /// <summary>Keeps every subscription's handler, keyed by topic.</summary>
    private sealed class TopicSubscriber : IMessageSubscriber
    {
        private readonly Dictionary<string, Func<MessageEnvelope, CancellationToken, Task<MessageResult>>> _handlers = new();

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

        public ValueTask DisposeAsync() => default;

        private sealed class NoopSubscription : ISubscription
        {
            public string Topic => string.Empty;
            public string SubscriptionName => string.Empty;
            public bool IsActive => true;
            public ValueTask DisposeAsync() => default;
        }
    }

    private sealed class CapturingPublisher : IMessagePublisher
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
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => default;
    }
}
