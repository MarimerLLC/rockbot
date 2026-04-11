using System.Text.Json;
using A2A;
using Microsoft.Extensions.Options;

using A2AAgentCard = A2A.AgentCard;
using A2AAgentSkill = A2A.AgentSkill;

namespace RockBot.A2A.Gateway;

/// <summary>
/// Routes A2A v1 JSON-RPC requests to the appropriate <see cref="A2AServer"/> method.
/// Streaming methods (<c>SendStreamingMessage</c>, <c>SubscribeToTask</c>) write SSE
/// directly to the response and return <c>null</c>; non-streaming methods return <see cref="IResult"/>.
/// Push notification CRUD and extended agent card are handled directly (the SDK's
/// <see cref="A2AServer"/> does not support these out of the box).
/// </summary>
internal static class JsonRpcRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Parses the JSON-RPC request, dispatches to the matching <see cref="A2AServer"/> method,
    /// and returns an <see cref="IResult"/> for synchronous methods or <c>null</c> when the
    /// response was already written as SSE.
    /// </summary>
    public static async Task<IResult?> HandleAsync(
        HttpRequest request,
        HttpResponse response,
        A2AServer server,
        IOptions<GatewayOptions> gatewayOptions,
        FilePushNotificationConfigStore pushConfigStore,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("A2A.Gateway.JsonRpc");

        string bodyJson;
        using (var reader = new StreamReader(request.Body))
            bodyJson = await reader.ReadToEndAsync(ct);

        JsonElement root;
        try
        {
            var doc = JsonDocument.Parse(bodyJson);
            root = doc.RootElement;
        }
        catch (JsonException)
        {
            return JsonRpcError(null, -32700, "Parse error");
        }

        var method = root.TryGetProperty("method", out var methodProp) ? methodProp.GetString() : null;
        var idRaw = root.TryGetProperty("id", out var idProp) ? idProp.GetRawText() : "null";

        if (method is null)
            return JsonRpcError(idRaw, -32600, "Invalid request: missing 'method'");

        logger.LogInformation("A2A JSON-RPC request: method={Method}", method);

        if (!root.TryGetProperty("params", out var paramsElement))
            return JsonRpcError(idRaw, -32600, "Invalid request: missing 'params'");

        var paramsJson = paramsElement.GetRawText();

        try
        {
            return method switch
            {
                // ── Core message methods ────────────────────────────────────
                "message/send" or A2AMethods.SendMessage =>
                    await HandleSendMessageAsync(server, paramsJson, idRaw, ct),

                "message/sendStream" or A2AMethods.SendStreamingMessage =>
                    await HandleSendStreamingMessageAsync(response, server, paramsJson, idRaw, logger, ct),

                // ── Task methods ────────────────────────────────────────────
                A2AMethods.GetTask =>
                    await HandleGetTaskAsync(server, paramsJson, idRaw, ct),

                A2AMethods.ListTasks =>
                    await HandleListTasksAsync(server, paramsJson, idRaw, ct),

                A2AMethods.CancelTask =>
                    await HandleCancelTaskAsync(server, paramsJson, idRaw, ct),

                A2AMethods.SubscribeToTask =>
                    await HandleSubscribeToTaskAsync(response, server, paramsJson, idRaw, logger, ct),

                // ── Push notification config CRUD ───────────────────────────
                A2AMethods.CreateTaskPushNotificationConfig =>
                    await HandleCreatePushNotificationConfigAsync(pushConfigStore, paramsJson, idRaw, ct),

                A2AMethods.GetTaskPushNotificationConfig =>
                    await HandleGetPushNotificationConfigAsync(pushConfigStore, paramsJson, idRaw, ct),

                A2AMethods.ListTaskPushNotificationConfig =>
                    await HandleListPushNotificationConfigAsync(pushConfigStore, paramsJson, idRaw, ct),

                A2AMethods.DeleteTaskPushNotificationConfig =>
                    await HandleDeletePushNotificationConfigAsync(pushConfigStore, paramsJson, idRaw, ct),

                // ── Agent card ──────────────────────────────────────────────
                A2AMethods.GetExtendedAgentCard =>
                    HandleGetExtendedAgentCard(gatewayOptions.Value, idRaw),

                _ => JsonRpcError(idRaw, -32601, $"Method not found: {method}")
            };
        }
        catch (A2AException ex)
        {
            logger.LogWarning(ex, "A2A error handling {Method}", method);
            return JsonRpcError(idRaw, (int)ex.ErrorCode, ex.Message);
        }
        catch (TimeoutException)
        {
            return JsonRpcError(idRaw, -32000, "Request timed out waiting for agent response");
        }
    }

    // ── Core message handlers ───────────────────────────────────────────────

    private static async Task<IResult> HandleSendMessageAsync(
        A2AServer server, string paramsJson, string idRaw, CancellationToken ct)
    {
        var sendRequest = JsonSerializer.Deserialize<SendMessageRequest>(paramsJson, JsonOptions);
        if (sendRequest is null)
            return JsonRpcError(idRaw, -32600, "Invalid SendMessage params");

        var response = await server.SendMessageAsync(sendRequest, ct);
        return JsonRpcResult(idRaw, response);
    }

    private static async Task<IResult?> HandleSendStreamingMessageAsync(
        HttpResponse httpResponse, A2AServer server, string paramsJson, string idRaw,
        ILogger logger, CancellationToken ct)
    {
        var sendRequest = JsonSerializer.Deserialize<SendMessageRequest>(paramsJson, JsonOptions);
        if (sendRequest is null)
            return JsonRpcError(idRaw, -32600, "Invalid SendStreamingMessage params");

        var events = server.SendStreamingMessageAsync(sendRequest, ct);
        await SseWriter.WriteStreamAsync(httpResponse, idRaw, events, logger, ct);
        return null; // Response already written as SSE
    }

    // ── Task handlers ───────────────────────────────────────────────────────

    private static async Task<IResult> HandleGetTaskAsync(
        A2AServer server, string paramsJson, string idRaw, CancellationToken ct)
    {
        var getRequest = JsonSerializer.Deserialize<GetTaskRequest>(paramsJson, JsonOptions);
        if (getRequest is null)
            return JsonRpcError(idRaw, -32600, "Invalid GetTask params");

        var task = await server.GetTaskAsync(getRequest, ct);
        return JsonRpcResult(idRaw, task);
    }

    private static async Task<IResult> HandleListTasksAsync(
        A2AServer server, string paramsJson, string idRaw, CancellationToken ct)
    {
        var listRequest = JsonSerializer.Deserialize<ListTasksRequest>(paramsJson, JsonOptions);
        if (listRequest is null)
            return JsonRpcError(idRaw, -32600, "Invalid ListTasks params");

        var response = await server.ListTasksAsync(listRequest, ct);
        return JsonRpcResult(idRaw, response);
    }

    private static async Task<IResult> HandleCancelTaskAsync(
        A2AServer server, string paramsJson, string idRaw, CancellationToken ct)
    {
        var cancelRequest = JsonSerializer.Deserialize<CancelTaskRequest>(paramsJson, JsonOptions);
        if (cancelRequest is null)
            return JsonRpcError(idRaw, -32600, "Invalid CancelTask params");

        var task = await server.CancelTaskAsync(cancelRequest, ct);
        return JsonRpcResult(idRaw, task);
    }

    private static async Task<IResult?> HandleSubscribeToTaskAsync(
        HttpResponse httpResponse, A2AServer server, string paramsJson, string idRaw,
        ILogger logger, CancellationToken ct)
    {
        var subRequest = JsonSerializer.Deserialize<SubscribeToTaskRequest>(paramsJson, JsonOptions);
        if (subRequest is null)
            return JsonRpcError(idRaw, -32600, "Invalid SubscribeToTask params");

        var events = server.SubscribeToTaskAsync(subRequest, ct);
        await SseWriter.WriteStreamAsync(httpResponse, idRaw, events, logger, ct);
        return null; // Response already written as SSE
    }

    // ── Push notification config CRUD ───────────────────────────────────────
    // Handled directly via FilePushNotificationConfigStore — the SDK's A2AServer
    // does not have a push notification store wired up.

    private static async Task<IResult> HandleCreatePushNotificationConfigAsync(
        FilePushNotificationConfigStore store, string paramsJson, string idRaw, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<CreateTaskPushNotificationConfigRequest>(paramsJson, JsonOptions);
        if (req is null)
            return JsonRpcError(idRaw, -32600, "Invalid CreateTaskPushNotificationConfig params");

        var configId = req.ConfigId ?? Guid.NewGuid().ToString("N");
        var config = await store.CreateAsync(req.TaskId, configId, req.Tenant ?? string.Empty, req.Config, ct);
        return JsonRpcResult(idRaw, config);
    }

    private static async Task<IResult> HandleGetPushNotificationConfigAsync(
        FilePushNotificationConfigStore store, string paramsJson, string idRaw, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<GetTaskPushNotificationConfigRequest>(paramsJson, JsonOptions);
        if (req is null)
            return JsonRpcError(idRaw, -32600, "Invalid GetTaskPushNotificationConfig params");

        var config = await store.GetAsync(req.Id, ct);
        if (config is null)
            return JsonRpcError(idRaw, -32001, $"Push notification config '{req.Id}' not found");
        return JsonRpcResult(idRaw, config);
    }

    private static async Task<IResult> HandleListPushNotificationConfigAsync(
        FilePushNotificationConfigStore store, string paramsJson, string idRaw, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<ListTaskPushNotificationConfigRequest>(paramsJson, JsonOptions);
        if (req is null)
            return JsonRpcError(idRaw, -32600, "Invalid ListTaskPushNotificationConfig params");

        var (configs, nextPageToken) = await store.ListAsync(req.TaskId, req.PageSize, req.PageToken, ct);
        return JsonRpcResult(idRaw, new ListTaskPushNotificationConfigResponse
        {
            Configs = configs,
            NextPageToken = nextPageToken
        });
    }

    private static async Task<IResult> HandleDeletePushNotificationConfigAsync(
        FilePushNotificationConfigStore store, string paramsJson, string idRaw, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<DeleteTaskPushNotificationConfigRequest>(paramsJson, JsonOptions);
        if (req is null)
            return JsonRpcError(idRaw, -32600, "Invalid DeleteTaskPushNotificationConfig params");

        await store.DeleteAsync(req.Id, ct);
        return JsonRpcResult(idRaw, new { });
    }

    // ── Agent card ──────────────────────────────────────────────────────────
    // Handled directly from GatewayOptions — the SDK's A2AServer does not
    // support extended agent cards out of the box.

    private static IResult HandleGetExtendedAgentCard(GatewayOptions config, string idRaw)
    {
        var card = new A2AAgentCard
        {
            Name = config.AgentName,
            Description = config.Description ?? string.Empty,
            Version = config.Version ?? "1.0",
            Capabilities = new AgentCapabilities
            {
                Streaming = true,
                PushNotifications = true,
                ExtendedAgentCard = true
            },
            Skills = config.Skills.Select(s => new A2AAgentSkill
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description ?? string.Empty
            }).ToList()
        };
        return JsonRpcResult(idRaw, card);
    }

    // ── JSON-RPC response helpers ───────────────────────────────────────────

    private static IResult JsonRpcResult(string idRaw, object result)
    {
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        return Results.Text(
            $$$"""{"jsonrpc":"2.0","id":{{{idRaw}}},"result":{{{resultJson}}}}""",
            "application/json");
    }

    private static IResult JsonRpcError(string? idRaw, int code, string message)
    {
        var escapedMessage = JsonSerializer.Serialize(message);
        return Results.Text(
            $$$"""{"jsonrpc":"2.0","id":{{{idRaw ?? "null"}}},"error":{"code":{{{code}}},"message":{{{escapedMessage}}}}}""",
            "application/json");
    }
}
