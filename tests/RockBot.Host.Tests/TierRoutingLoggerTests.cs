using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public class TierRoutingLoggerTests
{
    private static (TierRoutingLogger logger, string dir) CreateLogger()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rb-routing-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var logger = new TierRoutingLogger(
            Options.Create(new AgentProfileOptions { BasePath = dir }),
            NullLogger<TierRoutingLogger>.Instance);
        return (logger, dir);
    }

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
