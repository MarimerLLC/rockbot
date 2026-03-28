using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

[TestClass]
public class FileWipTrackerTests
{
    private string _tempDir = null!;
    private FileWipTracker _tracker = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-wip-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _tracker = CreateTracker(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task BeginAsync_CreatesFile()
    {
        var envelope = CreateTestEnvelope();

        var entry = await _tracker.BeginAsync(envelope);

        var expectedPath = Path.Combine(_tempDir, $"{envelope.MessageId}.json");
        Assert.IsTrue(File.Exists(expectedPath));
        Assert.AreEqual(envelope.MessageId, entry.MessageId);
        Assert.AreEqual(envelope.MessageType, entry.MessageType);
    }

    [TestMethod]
    public async Task CompleteAsync_DeletesFile()
    {
        var envelope = CreateTestEnvelope();
        await _tracker.BeginAsync(envelope);

        await _tracker.CompleteAsync(envelope.MessageId);

        var expectedPath = Path.Combine(_tempDir, $"{envelope.MessageId}.json");
        Assert.IsFalse(File.Exists(expectedPath));
    }

    [TestMethod]
    public async Task CompleteAsync_IsIdempotent()
    {
        var envelope = CreateTestEnvelope();
        await _tracker.BeginAsync(envelope);

        await _tracker.CompleteAsync(envelope.MessageId);
        await _tracker.CompleteAsync(envelope.MessageId); // Should not throw
    }

    [TestMethod]
    public async Task AbandonAsync_DeletesFile()
    {
        var envelope = CreateTestEnvelope();
        await _tracker.BeginAsync(envelope);

        await _tracker.AbandonAsync(envelope.MessageId, "test reason");

        var expectedPath = Path.Combine(_tempDir, $"{envelope.MessageId}.json");
        Assert.IsFalse(File.Exists(expectedPath));
    }

    [TestMethod]
    public async Task GetIncompleteAsync_ReturnsAllEntries()
    {
        var env1 = CreateTestEnvelope();
        var env2 = CreateTestEnvelope();

        await _tracker.BeginAsync(env1);
        await _tracker.BeginAsync(env2);

        var entries = await _tracker.GetIncompleteAsync();

        Assert.AreEqual(2, entries.Count);
        CollectionAssert.AreEquivalent(
            new[] { env1.MessageId, env2.MessageId },
            entries.Select(e => e.MessageId).ToArray());
    }

    [TestMethod]
    public async Task GetIncompleteAsync_ReturnsEmpty_WhenNoEntries()
    {
        var entries = await _tracker.GetIncompleteAsync();

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task GetIncompleteAsync_ExcludesCompleted()
    {
        var env1 = CreateTestEnvelope();
        var env2 = CreateTestEnvelope();

        await _tracker.BeginAsync(env1);
        await _tracker.BeginAsync(env2);
        await _tracker.CompleteAsync(env1.MessageId);

        var entries = await _tracker.GetIncompleteAsync();

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(env2.MessageId, entries[0].MessageId);
    }

    [TestMethod]
    public async Task RoundTrip_PreservesEnvelopeData()
    {
        var headers = new Dictionary<string, string> { ["x-test"] = "hello" };
        var bodyBytes = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var envelope = new MessageEnvelope
        {
            MessageId = Guid.NewGuid().ToString("N"),
            MessageType = "Test.Message",
            CorrelationId = "corr-123",
            ReplyTo = "reply-topic",
            Source = "test-source",
            Destination = "test-dest",
            Timestamp = DateTimeOffset.UtcNow,
            Body = bodyBytes,
            Headers = headers
        };

        await _tracker.BeginAsync(envelope);

        // Create a new tracker instance to force re-read from disk
        var tracker2 = CreateTracker(_tempDir);
        var entries = await tracker2.GetIncompleteAsync();

        Assert.AreEqual(1, entries.Count);
        var entry = entries[0];
        Assert.AreEqual(envelope.MessageId, entry.MessageId);
        Assert.AreEqual(envelope.MessageType, entry.MessageType);
        Assert.AreEqual(envelope.CorrelationId, entry.CorrelationId);
        Assert.AreEqual(envelope.ReplyTo, entry.ReplyTo);
        Assert.AreEqual(envelope.Source, entry.Source);
        Assert.AreEqual(envelope.Destination, entry.Destination);
        Assert.AreEqual("hello", entry.Headers["x-test"]);
        CollectionAssert.AreEqual(bodyBytes, entry.Body.ToArray());
    }

    [TestMethod]
    public async Task BeginAsync_SetsStartedAt()
    {
        var before = DateTimeOffset.UtcNow;
        var envelope = CreateTestEnvelope();

        var entry = await _tracker.BeginAsync(envelope);

        Assert.IsTrue(entry.StartedAt >= before);
        Assert.IsTrue(entry.StartedAt <= DateTimeOffset.UtcNow);
    }

    private static MessageEnvelope CreateTestEnvelope() =>
        MessageEnvelope.Create("TestType", new byte[] { 0x42 }, "test-source");

    private static FileWipTracker CreateTracker(string basePath) =>
        new(
            Options.Create(new WipOptions { BasePath = basePath }),
            Options.Create(new AgentProfileOptions { BasePath = basePath }),
            NullLogger<FileWipTracker>.Instance);
}
