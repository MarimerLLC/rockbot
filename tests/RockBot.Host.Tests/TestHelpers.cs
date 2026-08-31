using System.Text.Json;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

/// <summary>
/// Shared test helpers for creating test envelopes.
/// </summary>
internal static class TestHelpers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static MessageEnvelope CreateEnvelope<T>(T payload, string? messageType = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return MessageEnvelope.Create(
            messageType: messageType ?? typeof(T).FullName ?? typeof(T).Name,
            body: body,
            source: "test-source");
    }

    public static MessageEnvelope CreateEnvelopeWithRawBody(string messageType, byte[] body)
    {
        return MessageEnvelope.Create(
            messageType: messageType,
            body: body,
            source: "test-source");
    }
}

/// <summary>
/// A simple test message type.
/// </summary>
public record PingMessage(string Text);

/// <summary>
/// Another test message type.
/// </summary>
public record PongMessage(string Reply);
