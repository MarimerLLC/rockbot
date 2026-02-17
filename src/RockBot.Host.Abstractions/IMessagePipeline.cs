using RockBot.Messaging;

namespace RockBot.Host;

/// <summary>
/// Seam between the subscriber callback and the message processing pipeline.
/// </summary>
public interface IMessagePipeline
{
    /// <summary>
    /// Dispatch a message envelope through the middleware pipeline to the appropriate handler.
    /// </summary>
    Task<MessageResult> DispatchAsync(MessageEnvelope envelope, CancellationToken cancellationToken);
}
