namespace RockBot.Host;

/// <summary>
/// Handles a typed message. Set <see cref="MessageHandlerContext.Result"/>
/// to override the default Ack.
/// </summary>
/// <typeparam name="TMessage">The message payload type.</typeparam>
public interface IMessageHandler<in TMessage>
{
    /// <summary>
    /// Handle a deserialized message.
    /// </summary>
    Task HandleAsync(TMessage message, MessageHandlerContext context);
}
