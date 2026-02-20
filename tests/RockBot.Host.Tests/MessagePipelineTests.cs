using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

[TestClass]
public class MessagePipelineTests
{
    [TestMethod]
    public async Task DispatchAsync_RoutesToHandler()
    {
        var handler = new TrackingHandler<PingMessage>();
        var pipeline = BuildPipeline(resolver =>
        {
            resolver.Register<PingMessage>();
        }, services =>
        {
            services.AddSingleton<IMessageHandler<PingMessage>>(handler);
        });

        var envelope = TestHelpers.CreateEnvelope(new PingMessage("hello"));
        var result = await pipeline.DispatchAsync(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result);
        Assert.AreEqual(1, handler.Invocations.Count);
        Assert.AreEqual("hello", handler.Invocations[0].Text);
    }

    [TestMethod]
    public async Task DispatchAsync_UnknownType_ReturnsDeadLetter()
    {
        var pipeline = BuildPipeline(_ => { }, _ => { });

        var envelope = TestHelpers.CreateEnvelopeWithRawBody("unknown.type", [1, 2, 3]);
        var result = await pipeline.DispatchAsync(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.DeadLetter, result);
    }

    [TestMethod]
    public async Task DispatchAsync_MalformedJson_ReturnsDeadLetter()
    {
        var pipeline = BuildPipeline(resolver =>
        {
            resolver.Register<PingMessage>();
        }, services =>
        {
            services.AddSingleton<IMessageHandler<PingMessage>>(new TrackingHandler<PingMessage>());
        });

        var envelope = TestHelpers.CreateEnvelopeWithRawBody(
            typeof(PingMessage).FullName!, "not json"u8.ToArray());
        var result = await pipeline.DispatchAsync(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.DeadLetter, result);
    }

    [TestMethod]
    public async Task DispatchAsync_NullPayload_ReturnsDeadLetter()
    {
        var pipeline = BuildPipeline(resolver =>
        {
            resolver.Register<PingMessage>();
        }, services =>
        {
            services.AddSingleton<IMessageHandler<PingMessage>>(new TrackingHandler<PingMessage>());
        });

        var envelope = TestHelpers.CreateEnvelopeWithRawBody(
            typeof(PingMessage).FullName!, "null"u8.ToArray());
        var result = await pipeline.DispatchAsync(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.DeadLetter, result);
    }

    [TestMethod]
    public async Task DispatchAsync_DefaultResult_IsAck()
    {
        var pipeline = BuildPipeline(resolver =>
        {
            resolver.Register<PingMessage>();
        }, services =>
        {
            services.AddSingleton<IMessageHandler<PingMessage>>(new TrackingHandler<PingMessage>());
        });

        var envelope = TestHelpers.CreateEnvelope(new PingMessage("test"));
        var result = await pipeline.DispatchAsync(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result);
    }

    [TestMethod]
    public async Task DispatchAsync_HandlerCanSetResult()
    {
        var handler = new ResultSettingHandler(MessageResult.Retry);
        var pipeline = BuildPipeline(resolver =>
        {
            resolver.Register<PingMessage>();
        }, services =>
        {
            services.AddSingleton<IMessageHandler<PingMessage>>(handler);
        });

        var envelope = TestHelpers.CreateEnvelope(new PingMessage("test"));
        var result = await pipeline.DispatchAsync(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.Retry, result);
    }

    [TestMethod]
    public async Task DispatchAsync_CreatesScopePerMessage()
    {
        var scopeTracker = new ScopeTracker();
        var pipeline = BuildPipeline(resolver =>
        {
            resolver.Register<PingMessage>();
        }, services =>
        {
            services.AddScoped<IMessageHandler<PingMessage>>(_ =>
            {
                scopeTracker.ScopeCount++;
                return new TrackingHandler<PingMessage>();
            });
        });

        var envelope = TestHelpers.CreateEnvelope(new PingMessage("test"));
        await pipeline.DispatchAsync(envelope, CancellationToken.None);
        await pipeline.DispatchAsync(envelope, CancellationToken.None);

        Assert.AreEqual(2, scopeTracker.ScopeCount);
    }

    [TestMethod]
    public async Task DispatchAsync_NoHandlerRegistered_ReturnsDeadLetter()
    {
        var pipeline = BuildPipeline(resolver =>
        {
            resolver.Register<PingMessage>();
        }, _ => { }); // type registered but no handler

        var envelope = TestHelpers.CreateEnvelope(new PingMessage("test"));
        var result = await pipeline.DispatchAsync(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.DeadLetter, result);
    }

    private static MessagePipeline BuildPipeline(
        Action<MessageTypeResolver> configureResolver,
        Action<IServiceCollection> configureServices,
        IEnumerable<MiddlewareRegistration>? middleware = null)
    {
        var resolver = new MessageTypeResolver();
        configureResolver(resolver);

        var services = new ServiceCollection();
        services.AddLogging();
        configureServices(services);
        var provider = services.BuildServiceProvider();

        return new MessagePipeline(
            provider.GetRequiredService<IServiceScopeFactory>(),
            resolver,
            new AgentIdentity("test-agent"),
            middleware ?? [],
            provider.GetRequiredService<ILogger<MessagePipeline>>());
    }

    private sealed class TrackingHandler<T> : IMessageHandler<T>
    {
        public List<T> Invocations { get; } = [];

        public Task HandleAsync(T message, MessageHandlerContext context)
        {
            Invocations.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ResultSettingHandler(MessageResult result) : IMessageHandler<PingMessage>
    {
        public Task HandleAsync(PingMessage message, MessageHandlerContext context)
        {
            context.Result = result;
            return Task.CompletedTask;
        }
    }

    private sealed class ScopeTracker
    {
        public int ScopeCount { get; set; }
    }
}
