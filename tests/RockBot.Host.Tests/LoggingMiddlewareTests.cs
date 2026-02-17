using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Host.Middleware;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

[TestClass]
public class LoggingMiddlewareTests
{
    [TestMethod]
    public async Task LogsEntryAndExit()
    {
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(logger);
        var context = CreateContext();

        MessageHandlerDelegate next = _ => Task.CompletedTask;

        await middleware.InvokeAsync(context, next);

        Assert.IsTrue(logger.Messages.Count >= 2, $"Expected at least 2 log entries, got {logger.Messages.Count}");
        Assert.IsTrue(logger.Messages.Any(m => m.Contains("Dispatching")),
            "Expected a 'Dispatching' log entry");
        Assert.IsTrue(logger.Messages.Any(m => m.Contains("Completed")),
            "Expected a 'Completed' log entry");
    }

    [TestMethod]
    public async Task LogsMessageId()
    {
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(logger);
        var context = CreateContext();
        var messageId = context.Envelope.MessageId;

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.IsTrue(logger.Messages.Any(m => m.Contains(messageId)),
            "Expected message ID in log output");
    }

    [TestMethod]
    public async Task CallsNext()
    {
        var logger = new TestLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(logger);
        var context = CreateContext();
        var nextCalled = false;

        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.IsTrue(nextCalled);
    }

    private static MessageHandlerContext CreateContext() => new()
    {
        Envelope = MessageEnvelope.Create("test.type", new byte[] { 1 }, "src"),
        Agent = new AgentIdentity("test-agent"),
        Services = new EmptyServiceProvider(),
        CancellationToken = CancellationToken.None
    };

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
