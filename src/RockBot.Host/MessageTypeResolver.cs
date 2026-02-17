using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;

namespace RockBot.Host;

/// <summary>
/// Delegate that deserializes and dispatches a message to its typed handler.
/// Captured at registration time so no reflection is needed at dispatch time.
/// </summary>
internal delegate Task MessageDispatchDelegate(
    MessageEnvelope envelope,
    IServiceProvider services,
    MessageHandlerContext context,
    ILogger logger);

/// <summary>
/// Maps message type strings to CLR types and pre-built dispatch delegates.
/// All generic type information is captured at registration time, making
/// dispatch fully AOT-compatible.
/// </summary>
internal sealed class MessageTypeResolver : IMessageTypeResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a message type. Defaults the key to the type's FullName
    /// (matching the ToEnvelope convention). Captures a typed dispatch
    /// delegate for AOT-safe invocation.
    /// </summary>
    public void Register<T>(string? key = null)
    {
        var type = typeof(T);
        _registrations[key ?? type.FullName ?? type.Name] = new Registration(type, CreateDispatch<T>());
    }

    public Type? Resolve(string messageType)
    {
        return _registrations.TryGetValue(messageType, out var reg) ? reg.Type : null;
    }

    /// <summary>
    /// Get the pre-built dispatch delegate for a message type.
    /// </summary>
    internal MessageDispatchDelegate? GetDispatch(string messageType)
    {
        return _registrations.TryGetValue(messageType, out var reg) ? reg.Dispatch : null;
    }

    private static MessageDispatchDelegate CreateDispatch<T>() =>
        (envelope, services, context, logger) =>
        {
            T? payload;
            try
            {
                payload = JsonSerializer.Deserialize<T>(envelope.Body.Span, JsonOptions);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to deserialize message {MessageId} as {MessageType}",
                    envelope.MessageId, envelope.MessageType);
                context.Result = MessageResult.DeadLetter;
                return Task.CompletedTask;
            }

            if (payload is null)
            {
                logger.LogWarning("Deserialized null payload for message {MessageId}",
                    envelope.MessageId);
                context.Result = MessageResult.DeadLetter;
                return Task.CompletedTask;
            }

            var handler = services.GetService<IMessageHandler<T>>();
            if (handler is null)
            {
                logger.LogWarning("No handler registered for {MessageType}",
                    envelope.MessageType);
                context.Result = MessageResult.DeadLetter;
                return Task.CompletedTask;
            }

            return handler.HandleAsync(payload, context);
        };

    private readonly record struct Registration(Type Type, MessageDispatchDelegate Dispatch);
}
