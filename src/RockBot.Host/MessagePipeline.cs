using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;

namespace RockBot.Host;

/// <summary>
/// Dispatches messages through the middleware pipeline to typed handlers.
/// Creates a DI scope per message (like ASP.NET Core scope-per-request).
/// No reflection at dispatch time — all generic types are captured at registration.
/// </summary>
internal sealed class MessagePipeline : IMessagePipeline
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MessageTypeResolver _typeResolver;
    private readonly AgentIdentity _identity;
    private readonly List<Type> _middlewareTypes;
    private readonly ILogger<MessagePipeline> _logger;

    public MessagePipeline(
        IServiceScopeFactory scopeFactory,
        MessageTypeResolver typeResolver,
        AgentIdentity identity,
        IEnumerable<MiddlewareRegistration> middlewareRegistrations,
        ILogger<MessagePipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _typeResolver = typeResolver;
        _identity = identity;
        _middlewareTypes = middlewareRegistrations.Select(r => r.Type).ToList();
        _logger = logger;
    }

    public async Task<MessageResult> DispatchAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var context = new MessageHandlerContext
        {
            Envelope = envelope,
            Agent = _identity,
            Services = scope.ServiceProvider,
            CancellationToken = cancellationToken
        };

        // Build the pipeline: middleware chain → terminal handler
        MessageHandlerDelegate terminal = ctx => InvokeHandler(ctx, scope.ServiceProvider);
        var pipeline = BuildPipeline(scope.ServiceProvider, terminal);

        await pipeline(context);
        return context.Result;
    }

    private MessageHandlerDelegate BuildPipeline(IServiceProvider services, MessageHandlerDelegate terminal)
    {
        var pipeline = terminal;
        // Wrap in reverse order so first-registered middleware runs first
        for (var i = _middlewareTypes.Count - 1; i >= 0; i--)
        {
            var middlewareType = _middlewareTypes[i];
            var next = pipeline;
            pipeline = async ctx =>
            {
                var middleware = (IMiddleware)services.GetRequiredService(middlewareType);
                await middleware.InvokeAsync(ctx, next);
            };
        }
        return pipeline;
    }

    private Task InvokeHandler(MessageHandlerContext context, IServiceProvider services)
    {
        var dispatch = _typeResolver.GetDispatch(context.Envelope.MessageType);
        if (dispatch is null)
        {
            _logger.LogWarning("Unknown message type: {MessageType}", context.Envelope.MessageType);
            context.Result = MessageResult.DeadLetter;
            return Task.CompletedTask;
        }

        return dispatch(context.Envelope, services, context, _logger);
    }
}

/// <summary>
/// Marker registration for ordered middleware types.
/// </summary>
internal sealed record MiddlewareRegistration(Type Type);
