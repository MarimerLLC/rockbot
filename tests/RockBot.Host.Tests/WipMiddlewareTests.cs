using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Host.Middleware;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

[TestClass]
public class WipMiddlewareTests
{
    private string _tempDir = null!;
    private FileWipTracker _tracker = null!;
    private WipMiddleware _middleware = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-wip-mw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _tracker = new FileWipTracker(
            Options.Create(new WipOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions { BasePath = _tempDir }),
            NullLogger<FileWipTracker>.Instance);

        _middleware = new WipMiddleware(_tracker, NullLogger<WipMiddleware>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task SynchronousHandler_AutoCompletes()
    {
        var context = CreateContext();
        MessageHandlerDelegate next = _ => Task.CompletedTask;

        await _middleware.InvokeAsync(context, next);

        // WIP entry should be auto-completed — no files remain
        var entries = await _tracker.GetIncompleteAsync();
        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task DeferredHandler_DoesNotAutoComplete()
    {
        var context = CreateContext();
        MessageHandlerDelegate next = ctx =>
        {
            ctx.Items[WipConstants.DeferredKey] = true;
            return Task.CompletedTask;
        };

        await _middleware.InvokeAsync(context, next);

        // WIP entry should remain because handler deferred completion
        var entries = await _tracker.GetIncompleteAsync();
        Assert.AreEqual(1, entries.Count);
    }

    [TestMethod]
    public async Task HandlerThrows_WipRemains()
    {
        var context = CreateContext();
        MessageHandlerDelegate next = _ => throw new InvalidOperationException("boom");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _middleware.InvokeAsync(context, next));

        // WIP entry should remain for recovery
        var entries = await _tracker.GetIncompleteAsync();
        Assert.AreEqual(1, entries.Count);
    }

    [TestMethod]
    public async Task MessageId_ExposedInContextItems()
    {
        var context = CreateContext();
        string? capturedId = null;
        MessageHandlerDelegate next = ctx =>
        {
            capturedId = ctx.Items.TryGetValue(WipConstants.MessageIdKey, out var id)
                ? (string)id : null;
            return Task.CompletedTask;
        };

        await _middleware.InvokeAsync(context, next);

        Assert.IsNotNull(capturedId);
        Assert.AreEqual(context.Envelope.MessageId, capturedId);
    }

    [TestMethod]
    public async Task DeferredThenExplicitComplete_CleansUp()
    {
        var context = CreateContext();
        MessageHandlerDelegate next = ctx =>
        {
            ctx.Items[WipConstants.DeferredKey] = true;
            return Task.CompletedTask;
        };

        await _middleware.InvokeAsync(context, next);

        // Simulate background loop calling CompleteAsync
        var messageId = (string)context.Items[WipConstants.MessageIdKey];
        await _tracker.CompleteAsync(messageId);

        var entries = await _tracker.GetIncompleteAsync();
        Assert.AreEqual(0, entries.Count);
    }

    private static MessageHandlerContext CreateContext() => new()
    {
        Envelope = MessageEnvelope.Create("test", new byte[] { 1 }, "src"),
        Agent = new AgentIdentity("test-agent"),
        Services = new EmptyServiceProvider(),
        CancellationToken = CancellationToken.None
    };

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
