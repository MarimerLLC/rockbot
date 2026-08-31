using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

[TestClass]
public class MiddlewarePipelineTests
{
    [TestMethod]
    public async Task Middleware_ExecutesInRegistrationOrder()
    {
        var order = new List<string>();

        var pipeline = BuildPipelineWithMiddleware(
        [
            typeof(OrderTrackingMiddlewareA),
            typeof(OrderTrackingMiddlewareB)
        ], services =>
        {
            services.AddSingleton(order);
            services.AddScoped<OrderTrackingMiddlewareA>();
            services.AddScoped<OrderTrackingMiddlewareB>();
            services.AddSingleton<IMessageHandler<PingMessage>>(new NoOpHandler());
        });

        var envelope = TestHelpers.CreateEnvelope(new PingMessage("test"));
        await pipeline.DispatchAsync(envelope, CancellationToken.None);

        Assert.AreEqual(4, order.Count);
        Assert.AreEqual("A-before", order[0]);
        Assert.AreEqual("B-before", order[1]);
        Assert.AreEqual("B-after", order[2]);
        Assert.AreEqual("A-after", order[3]);
    }

    [TestMethod]
    public async Task Middleware_CanShortCircuit()
    {
        var handlerCalled = false;
        var pipeline = BuildPipelineWithMiddleware(
        [
            typeof(ShortCircuitMiddleware)
        ], services =>
        {
            services.AddScoped<ShortCircuitMiddleware>();
            services.AddSingleton<IMessageHandler<PingMessage>>(new CallbackHandler(_ => handlerCalled = true));
        });

        var envelope = TestHelpers.CreateEnvelope(new PingMessage("test"));
        var result = await pipeline.DispatchAsync(envelope, CancellationToken.None);

        Assert.IsFalse(handlerCalled);
        Assert.AreEqual(MessageResult.DeadLetter, result);
    }

    [TestMethod]
    public async Task Middleware_CanModifyResultAfterHandler()
    {
        var pipeline = BuildPipelineWithMiddleware(
        [
            typeof(ResultOverridingMiddleware)
        ], services =>
        {
            services.AddScoped<ResultOverridingMiddleware>();
            services.AddSingleton<IMessageHandler<PingMessage>>(new NoOpHandler());
        });

        var envelope = TestHelpers.CreateEnvelope(new PingMessage("test"));
        var result = await pipeline.DispatchAsync(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.Retry, result);
    }

    private static MessagePipeline BuildPipelineWithMiddleware(
        Type[] middlewareTypes,
        Action<IServiceCollection> configureServices)
    {
        var resolver = new MessageTypeResolver();
        resolver.Register<PingMessage>();

        var services = new ServiceCollection();
        services.AddLogging();
        configureServices(services);
        var provider = services.BuildServiceProvider();

        var registrations = middlewareTypes
            .Select(t => new MiddlewareRegistration(t))
            .ToList();

        return new MessagePipeline(
            provider.GetRequiredService<IServiceScopeFactory>(),
            resolver,
            new AgentIdentity("test-agent"),
            registrations,
            provider.GetRequiredService<ILogger<MessagePipeline>>());
    }

    private sealed class NoOpHandler : IMessageHandler<PingMessage>
    {
        public Task HandleAsync(PingMessage message, MessageHandlerContext context)
            => Task.CompletedTask;
    }

    private sealed class CallbackHandler(Action<PingMessage> callback) : IMessageHandler<PingMessage>
    {
        public Task HandleAsync(PingMessage message, MessageHandlerContext context)
        {
            callback(message);
            return Task.CompletedTask;
        }
    }

    public sealed class OrderTrackingMiddlewareA(List<string> order) : IMiddleware
    {
        public async Task InvokeAsync(MessageHandlerContext context, MessageHandlerDelegate next)
        {
            order.Add("A-before");
            await next(context);
            order.Add("A-after");
        }
    }

    public sealed class OrderTrackingMiddlewareB(List<string> order) : IMiddleware
    {
        public async Task InvokeAsync(MessageHandlerContext context, MessageHandlerDelegate next)
        {
            order.Add("B-before");
            await next(context);
            order.Add("B-after");
        }
    }

    public sealed class ShortCircuitMiddleware : IMiddleware
    {
        public Task InvokeAsync(MessageHandlerContext context, MessageHandlerDelegate next)
        {
            context.Result = MessageResult.DeadLetter;
            return Task.CompletedTask; // Don't call next
        }
    }

    public sealed class ResultOverridingMiddleware : IMiddleware
    {
        public async Task InvokeAsync(MessageHandlerContext context, MessageHandlerDelegate next)
        {
            await next(context);
            context.Result = MessageResult.Retry; // Override after handler
        }
    }
}
