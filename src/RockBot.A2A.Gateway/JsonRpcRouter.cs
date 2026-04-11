using System.Text.Json;
using A2A;

namespace RockBot.A2A.Gateway;

/// <summary>
/// Routes A2A v1 JSON-RPC requests to the appropriate <see cref="A2AServer"/> method.
/// </summary>
internal static class JsonRpcRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<IResult> HandleAsync(
        HttpRequest request,
        A2AServer server,
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
                "message/send" or A2AMethods.SendMessage =>
                    await HandleSendMessageAsync(server, paramsJson, idRaw, ct),

                A2AMethods.GetTask =>
                    await HandleGetTaskAsync(server, paramsJson, idRaw, ct),

                A2AMethods.CancelTask =>
                    await HandleCancelTaskAsync(server, paramsJson, idRaw, ct),

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

    private static async Task<IResult> HandleSendMessageAsync(
        A2AServer server, string paramsJson, string idRaw, CancellationToken ct)
    {
        var sendRequest = JsonSerializer.Deserialize<SendMessageRequest>(paramsJson, JsonOptions);
        if (sendRequest is null)
            return JsonRpcError(idRaw, -32600, "Invalid SendMessage params");

        var response = await server.SendMessageAsync(sendRequest, ct);
        return JsonRpcResult(idRaw, response);
    }

    private static async Task<IResult> HandleGetTaskAsync(
        A2AServer server, string paramsJson, string idRaw, CancellationToken ct)
    {
        var getRequest = JsonSerializer.Deserialize<GetTaskRequest>(paramsJson, JsonOptions);
        if (getRequest is null)
            return JsonRpcError(idRaw, -32600, "Invalid GetTask params");

        var task = await server.GetTaskAsync(getRequest, ct);
        return JsonRpcResult(idRaw, task);
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
