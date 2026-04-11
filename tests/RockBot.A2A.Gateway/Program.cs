using System.Text.Json;
using A2A;
using RockBot.Messaging;
using RockBot.Messaging.RabbitMQ;

// Alias to avoid conflicts between A2A SDK types and RockBot types
using RbAgentTaskRequest = RockBot.A2A.AgentTaskRequest;
using RbAgentTaskResult = RockBot.A2A.AgentTaskResult;
using RbAgentMessage = RockBot.A2A.AgentMessage;
using RbAgentMessagePart = RockBot.A2A.AgentMessagePart;

var builder = WebApplication.CreateBuilder(args);

// RabbitMQ connection to reach RockBot
builder.Services.AddRockBotRabbitMq(opts =>
{
    opts.HostName = Environment.GetEnvironmentVariable("RabbitMq__HostName") ?? "localhost";
    opts.Port = int.TryParse(Environment.GetEnvironmentVariable("RabbitMq__Port"), out var p) ? p : 5672;
    opts.UserName = Environment.GetEnvironmentVariable("RabbitMq__UserName") ?? "rockbot";
    opts.Password = Environment.GetEnvironmentVariable("RabbitMq__Password") ?? "rockbot";
});

// A2A v1 server components
builder.Services.AddSingleton<ITaskStore, InMemoryTaskStore>();
builder.Services.AddSingleton<ChannelEventNotifier>();
builder.Services.AddSingleton<IAgentHandler, RockBotBridgeHandler>();
builder.Services.AddSingleton(sp => new A2AServer(
    sp.GetRequiredService<IAgentHandler>(),
    sp.GetRequiredService<ITaskStore>(),
    sp.GetRequiredService<ChannelEventNotifier>(),
    sp.GetRequiredService<ILogger<A2AServer>>(),
    new A2AServerOptions()));

// A2A v1 agent card served via the gateway (using the SDK's AgentCard type)
var agentCard = new AgentCard
{
    Name = "RockBot",
    Description = "Personal AI agent — accepts notifications and availability queries",
    Version = "1.0",
    Skills =
    [
        new AgentSkill { Id = "notify-user", Name = "Notify User",
            Description = "Send a notification to the user" },
        new AgentSkill { Id = "query-availability", Name = "Query Availability",
            Description = "Check if the user is available (free/busy)" }
    ]
};

var app = builder.Build();

// A2A v1 discovery endpoint
app.MapGet("/.well-known/agent-card.json", () => Results.Json(agentCard));

// A2A v1 JSON-RPC endpoint
var jsonRpcOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
};

app.MapPost("/", async (HttpRequest request, A2AServer server, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("A2A.Gateway");

    // Read body as string to avoid JsonDocument disposal issues
    using var reader = new StreamReader(request.Body);
    var bodyJson = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);

    var doc = JsonDocument.Parse(bodyJson);
    var root = doc.RootElement;

    var method = root.GetProperty("method").GetString();
    var idRaw = root.TryGetProperty("id", out var idProp) ? idProp.GetRawText() : "null";

    logger.LogInformation("A2A JSON-RPC request: method={Method}", method);

    if (method is "message/send" or "SendMessage")
    {
        var paramsJson = root.GetProperty("params").GetRawText();
        var sendRequest = JsonSerializer.Deserialize<SendMessageRequest>(paramsJson, jsonRpcOptions);

        if (sendRequest is null)
            return Results.Text(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":-32600,\"message\":\"Invalid request\"}}}}",
                "application/json");

        var response = await server.SendMessageAsync(sendRequest, request.HttpContext.RequestAborted);

        var resultJson = JsonSerializer.Serialize(response, jsonRpcOptions);
        return Results.Text(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}",
            "application/json");
    }

    return Results.Text(
        $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":-32601,\"message\":\"Method not found: {method}\"}}}}",
        "application/json");
});

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
startupLogger.LogInformation("A2A Gateway starting — bridging HTTP A2A to RockBot via RabbitMQ");

await app.RunAsync();

/// <summary>
/// Bridges A2A v1 server requests to RockBot's RabbitMQ message handler.
/// Publishes AgentTaskRequest, waits for AgentTaskResult, then enqueues the response.
/// </summary>
internal sealed class RockBotBridgeHandler(
    IMessagePublisher publisher,
    IMessageSubscriber subscriber,
    ILogger<RockBotBridgeHandler> logger) : IAgentHandler
{
    private const string GatewayIdentity = "A2AGateway";
    private const string ReplyTopic = $"agent.response.{GatewayIdentity}";

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var taskId = context.TaskId ?? Guid.NewGuid().ToString("N");
        var skill = "general";
        if (context.Metadata?.TryGetValue("skill", out var skillEl) == true)
            skill = skillEl.GetString() ?? "general";

        var messageText = context.UserText ?? "(empty)";

        logger.LogInformation("Bridging A2A task {TaskId} skill={Skill} to RockBot via RabbitMQ", taskId, skill);

        // Subscribe for the response BEFORE publishing
        var resultTcs = new TaskCompletionSource<RbAgentTaskResult>();
        var subName = $"a2a-gw-{Guid.NewGuid():N}";
        await using var sub = await subscriber.SubscribeAsync(
            ReplyTopic,
            subName,
            (envelope, _) =>
            {
                try
                {
                    var result = envelope.GetPayload<RbAgentTaskResult>();
                    if (result?.TaskId == taskId)
                        resultTcs.TrySetResult(result);
                }
                catch { /* ignore */ }
                return Task.FromResult(MessageResult.Ack);
            },
            cancellationToken);

        // Brief delay for subscription to bind
        await Task.Delay(300, cancellationToken);

        // Publish task to RockBot
        var request = new RbAgentTaskRequest
        {
            TaskId = taskId,
            Skill = skill,
            Message = new RbAgentMessage
            {
                Role = "user",
                Parts = [new RbAgentMessagePart { Kind = "text", Text = messageText }]
            }
        };

        var envelope = request.ToEnvelope<RbAgentTaskRequest>(
            source: GatewayIdentity,
            correlationId: taskId,
            replyTo: ReplyTopic);

        await publisher.PublishAsync("agent.task.RockBot", envelope, cancellationToken);

        // Wait for RockBot's response
        var result = await resultTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

        logger.LogInformation("Got response for task {TaskId}: state={State}", taskId, result.State);

        // Map RockBot result back to A2A v1 Message
        var responseText = result.Message?.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault() ?? "(no response)";

        await eventQueue.EnqueueMessageAsync(new Message
        {
            Role = Role.Agent,
            Parts = [new Part { Text = responseText }]
        }, cancellationToken);
    }

    public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        logger.LogInformation("Cancel requested for task {TaskId}", context.TaskId);
        return Task.CompletedTask;
    }
}
