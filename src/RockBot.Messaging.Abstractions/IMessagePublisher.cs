namespace RockBot.Messaging;

/// <summary>
/// Publishes messages to a topic. Consumers subscribe to topics
/// they care about. This is the primary send-side abstraction.
/// </summary>
public interface IMessagePublisher : IAsyncDisposable
{
    /// <summary>
    /// Publish a message to a topic.
    /// </summary>
    /// <param name="topic">Routing topic (e.g. "agent.task", "agent.response").</param>
    /// <param name="envelope">The message envelope to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default);
}
