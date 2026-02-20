using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Host.Middleware;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

[TestClass]
public class ErrorHandlingMiddlewareTests
{
    private readonly ErrorHandlingMiddleware _middleware =
        new(NullLogger<ErrorHandlingMiddleware>.Instance);

    [TestMethod]
    public async Task CatchesException_ReturnsRetry()
    {
        var context = CreateContext();
        MessageHandlerDelegate next = _ => throw new InvalidOperationException("boom");

        await _middleware.InvokeAsync(context, next);

        Assert.AreEqual(MessageResult.Retry, context.Result);
    }

    [TestMethod]
    public async Task CatchesCancellation_WhenTokenCancelled_ReturnsRetry()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var context = CreateContext(cts.Token);

        MessageHandlerDelegate next = _ => throw new OperationCanceledException(cts.Token);

        await _middleware.InvokeAsync(context, next);

        Assert.AreEqual(MessageResult.Retry, context.Result);
    }

    [TestMethod]
    public async Task PassthroughOnSuccess()
    {
        var context = CreateContext();
        var nextCalled = false;
        MessageHandlerDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        await _middleware.InvokeAsync(context, next);

        Assert.IsTrue(nextCalled);
        Assert.AreEqual(MessageResult.Ack, context.Result);
    }

    [TestMethod]
    public async Task PreservesHandlerResult_OnSuccess()
    {
        var context = CreateContext();
        MessageHandlerDelegate next = ctx =>
        {
            ctx.Result = MessageResult.DeadLetter;
            return Task.CompletedTask;
        };

        await _middleware.InvokeAsync(context, next);

        Assert.AreEqual(MessageResult.DeadLetter, context.Result);
    }

    private static MessageHandlerContext CreateContext(CancellationToken ct = default) => new()
    {
        Envelope = MessageEnvelope.Create("test", new byte[] { 1 }, "src"),
        Agent = new AgentIdentity("test-agent"),
        Services = new EmptyServiceProvider(),
        CancellationToken = ct
    };

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
