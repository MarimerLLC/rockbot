namespace RockBot.Host;

/// <summary>
/// Delegate representing the next step in the middleware pipeline.
/// </summary>
public delegate Task MessageHandlerDelegate(MessageHandlerContext context);

/// <summary>
/// Middleware in the message processing pipeline. Follows the standard
/// chain-of-responsibility pattern.
/// </summary>
public interface IMiddleware
{
    /// <summary>
    /// Process the message and optionally call the next middleware.
    /// </summary>
    Task InvokeAsync(MessageHandlerContext context, MessageHandlerDelegate next);
}
