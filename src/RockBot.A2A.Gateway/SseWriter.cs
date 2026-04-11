using System.Text.Json;
using A2A;

namespace RockBot.A2A.Gateway;

/// <summary>
/// Writes <see cref="StreamResponse"/> events to an HTTP response as Server-Sent Events (SSE).
/// Each event is a JSON-RPC 2.0 result wrapping the <see cref="StreamResponse"/> payload.
/// </summary>
internal static class SseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Streams <see cref="StreamResponse"/> events as SSE to the given <see cref="HttpResponse"/>.
    /// Sets appropriate headers before writing. Each event is formatted as
    /// <c>data: {"jsonrpc":"2.0","id":...,"result":...}\n\n</c>.
    /// </summary>
    public static async Task WriteStreamAsync(
        HttpResponse response,
        string idRaw,
        IAsyncEnumerable<StreamResponse> events,
        ILogger logger,
        CancellationToken ct)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";

        try
        {
            await foreach (var evt in events.WithCancellation(ct))
            {
                var resultJson = JsonSerializer.Serialize(evt, JsonOptions);
                var line = $"data: {{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}\n\n";
                await response.WriteAsync(line, ct);
                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected — expected for SSE
            logger.LogDebug("SSE stream cancelled (client disconnected)");
        }
        catch (A2AException ex)
        {
            logger.LogWarning(ex, "A2A error during SSE stream");
            var escapedMessage = JsonSerializer.Serialize(ex.Message);
            var errorLine = $"data: {{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":{(int)ex.ErrorCode},\"message\":{escapedMessage}}}}}\n\n";
            try
            {
                await response.WriteAsync(errorLine, ct);
                await response.Body.FlushAsync(ct);
            }
            catch { /* response may already be closed */ }
        }
    }
}
