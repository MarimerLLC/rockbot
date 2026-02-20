using RockBot.Messaging;

namespace RockBot.Host;

/// <summary>
/// Per-message context passed through the middleware pipeline and into handlers.
/// </summary>
public sealed class MessageHandlerContext
{
    /// <summary>
    /// The raw message envelope being processed.
    /// </summary>
    public required MessageEnvelope Envelope { get; init; }

    /// <summary>
    /// Identity of the agent processing the message.
    /// </summary>
    public required AgentIdentity Agent { get; init; }

    /// <summary>
    /// Scoped service provider for this message's DI scope.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// Cancellation token for this message's processing.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// The result of processing this message. Defaults to Ack.
    /// Handlers and middleware can set this to override the default.
    /// </summary>
    public MessageResult Result { get; set; } = MessageResult.Ack;

    /// <summary>
    /// Mutable dictionary for middleware to pass data through the pipeline.
    /// </summary>
    public IDictionary<string, object> Items { get; } = new Dictionary<string, object>();
}
