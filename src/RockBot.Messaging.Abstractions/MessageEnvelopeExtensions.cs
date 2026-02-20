using System.Text.Json;

namespace RockBot.Messaging;

/// <summary>
/// Convenience extensions for working with MessageEnvelope payloads.
/// </summary>
public static class MessageEnvelopeExtensions
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Deserialize the envelope body to a typed payload.
    /// </summary>
    public static T? GetPayload<T>(this MessageEnvelope envelope, JsonSerializerOptions? options = null)
    {
        return JsonSerializer.Deserialize<T>(envelope.Body.Span, options ?? DefaultOptions);
    }

    /// <summary>
    /// Create an envelope from a typed payload, serializing it to the body.
    /// </summary>
    public static MessageEnvelope ToEnvelope<T>(
        this T payload,
        string source,
        string? correlationId = null,
        string? replyTo = null,
        string? destination = null,
        IReadOnlyDictionary<string, string>? headers = null,
        JsonSerializerOptions? options = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, options ?? DefaultOptions);
        return MessageEnvelope.Create(
            messageType: typeof(T).FullName ?? typeof(T).Name,
            body: body,
            source: source,
            correlationId: correlationId,
            replyTo: replyTo,
            destination: destination,
            headers: headers);
    }
}
