using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public class TierRoutingLoggerTests
{
    private static (TierRoutingLogger logger, string dir) CreateLogger(int? maxEntries = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rb-routing-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var options = new AgentProfileOptions { BasePath = dir };
        if (maxEntries.HasValue) options.TierRoutingLogMaxEntries = maxEntries.Value;
        var logger = new TierRoutingLogger(
            Options.Create(options),
            NullLogger<TierRoutingLogger>.Instance);
        return (logger, dir);
    }

    [TestMethod]
    public async Task ReadRecent_WithSince_ExcludesEntriesOutsideTheWindow()
    {
        // The log is append-only and readers take its tail, so without a window an agent that
        // has stopped routing anything still presents the same trailing entries on every dream
        // cycle — the review pass could never run out of input and stop.
        var (logger, dir) = CreateLogger();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await logger.AppendAsync(Entry(now.AddDays(-40)));
            await logger.AppendAsync(Entry(now.AddDays(-20)));
            await logger.AppendAsync(Entry(now.AddDays(-2)));

            var all = await logger.ReadRecentAsync();
            var windowed = await logger.ReadRecentAsync(200, now.AddDays(-14));

            Assert.AreEqual(3, all.Count, "No window should still return everything.");
            Assert.AreEqual(1, windowed.Count);
            Assert.IsTrue(windowed[0].Timestamp > now.AddDays(-14));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadRecent_WithSince_KeepsEntriesMissingATimestamp()
    {
        // Records written before Timestamp was populated deserialize to default(DateTimeOffset).
        // Dropping them on a window would silently discard history rather than age it out.
        var (logger, dir) = CreateLogger();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "tier-routing-log.jsonl"),
                """{"promptPreview":"legacy","tier":"Balanced","context":"user-message"}""" + "\n");

            var windowed = await logger.ReadRecentAsync(200, DateTimeOffset.UtcNow.AddDays(-14));

            Assert.AreEqual(1, windowed.Count);
            Assert.AreEqual("legacy", windowed[0].PromptPreview);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static TierRoutingEntry Entry(DateTimeOffset at) => new()
    {
        Timestamp = at,
        PromptPreview = "test prompt",
        Tier = ModelTier.Balanced,
        Context = "user-message",
        ComplexityScore = 0.42,
    };

    [TestMethod]
    public async Task AppendAndRead_RoundTripsModelId()
    {
        var (logger, dir) = CreateLogger();
        try
        {
            await logger.AppendAsync(new TierRoutingEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                PromptPreview = "test prompt",
                Tier = ModelTier.Balanced,
                Context = "user-message",
                ComplexityScore = 0.42,
                ModelId = "claude-sonnet-4-6",
            });

            var entries = await logger.ReadRecentAsync();
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("claude-sonnet-4-6", entries[0].ModelId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task AppendAndRead_NullModelId_RoundTripsAsNull()
    {
        var (logger, dir) = CreateLogger();
        try
        {
            await logger.AppendAsync(new TierRoutingEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                PromptPreview = "test prompt",
                Tier = ModelTier.Low,
                Context = "user-message",
                ComplexityScore = 0.10,
                ModelId = null,
            });

            var entries = await logger.ReadRecentAsync();
            Assert.AreEqual(1, entries.Count);
            Assert.IsNull(entries[0].ModelId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task AppendAsync_RespectsConfigurableCap_EvictsOldestEntries()
    {
        // With max=5, writing 7 entries should leave only the most recent 5.
        var (logger, dir) = CreateLogger(maxEntries: 5);
        try
        {
            for (var i = 0; i < 7; i++)
            {
                await logger.AppendAsync(new TierRoutingEntry
                {
                    Timestamp = DateTimeOffset.UtcNow.AddSeconds(i),
                    PromptPreview = $"entry-{i}",
                    Tier = ModelTier.Balanced,
                    Context = "user-message",
                    ComplexityScore = 0.30,
                });
            }

            var entries = await logger.ReadRecentAsync(maxResults: 100);
            Assert.AreEqual(5, entries.Count, "Cap should have evicted the oldest entries");
            Assert.AreEqual("entry-2", entries[0].PromptPreview);
            Assert.AreEqual("entry-6", entries[^1].PromptPreview);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Read_LegacyEntryWithoutModelId_ParsesWithNullModelId()
    {
        // Simulates an entry written by a pre-ModelId version of the agent.
        // The on-disk JSONL has no "modelId" property; deserialization must default to null.
        var (logger, dir) = CreateLogger();
        try
        {
            var legacyLine = """
                {"timestamp":"2026-05-22T10:00:00+00:00","promptPreview":"legacy","tier":"Balanced","context":"user-message","complexityScore":0.5,"matchedHighKeywords":[],"matchedLowKeywords":[],"isFallbackTriggered":false}
                """;
            await File.WriteAllTextAsync(Path.Combine(dir, "tier-routing-log.jsonl"), legacyLine + "\n");

            var entries = await logger.ReadRecentAsync();
            Assert.AreEqual(1, entries.Count);
            Assert.IsNull(entries[0].ModelId);
            Assert.AreEqual("legacy", entries[0].PromptPreview);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
