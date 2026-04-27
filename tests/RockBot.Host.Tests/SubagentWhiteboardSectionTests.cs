namespace RockBot.Host.Tests;

[TestClass]
public class SubagentWhiteboardSectionTests
{
    private static WorkingMemoryEntry Entry(
        string key,
        string value,
        DateTimeOffset? storedAt = null,
        string? category = null) =>
        new(
            Key: key,
            Value: value,
            StoredAt: storedAt ?? DateTimeOffset.UtcNow,
            ExpiresAt: (storedAt ?? DateTimeOffset.UtcNow).AddHours(4),
            Category: category,
            Tags: null);

    [TestMethod]
    public void EmptyEntries_ReturnsNull()
    {
        var section = DreamService.BuildSubagentWhiteboardSection(
            entries: [], perEntryCap: 1000, maxEntries: 50);

        Assert.IsNull(section,
            "Returning null when there's nothing to add lets the caller skip appending without checking length.");
    }

    [TestMethod]
    public void SingleEntry_AppearsWithKeyAndValue()
    {
        var entries = new[]
        {
            Entry(
                key: "subagent/abc123/communications-triage",
                value: """{"server":"onedrive-personal"}""",
                category: "briefing/communications")
        };

        var section = DreamService.BuildSubagentWhiteboardSection(entries, 1000, 50);

        Assert.IsNotNull(section);
        StringAssert.Contains(section, "[subagent/abc123/communications-triage]");
        StringAssert.Contains(section, "category=briefing/communications");
        StringAssert.Contains(section, "onedrive-personal");
    }

    [TestMethod]
    public void IncludesInstructionalHeader()
    {
        var entries = new[] { Entry("subagent/x/y", "value") };

        var section = DreamService.BuildSubagentWhiteboardSection(entries, 1000, 50);

        Assert.IsNotNull(section);
        StringAssert.Contains(section, "Subagent verified data");
        StringAssert.Contains(section, "verified — not speculation",
            "The header tells the miner these are tool-call-verified facts.");
    }

    [TestMethod]
    public void NullCategory_RendersAsUncategorized()
    {
        var entries = new[] { Entry("subagent/x/y", "value", category: null) };

        var section = DreamService.BuildSubagentWhiteboardSection(entries, 1000, 50);

        Assert.IsNotNull(section);
        StringAssert.Contains(section, "category=(uncategorized)");
    }

    [TestMethod]
    public void EmptyCategory_RendersAsUncategorized()
    {
        var entries = new[] { Entry("subagent/x/y", "value", category: "") };

        var section = DreamService.BuildSubagentWhiteboardSection(entries, 1000, 50);

        Assert.IsNotNull(section);
        StringAssert.Contains(section, "category=(uncategorized)");
    }

    [TestMethod]
    public void ValueLongerThanPerEntryCap_IsTruncatedWithMarker()
    {
        var longValue = new string('x', 5000);
        var entries = new[] { Entry("subagent/x/y", longValue) };

        var section = DreamService.BuildSubagentWhiteboardSection(entries, perEntryCap: 100, maxEntries: 50);

        Assert.IsNotNull(section);
        StringAssert.Contains(section, "…[truncated]");
        Assert.IsFalse(section.Contains(longValue),
            "The full value should not appear when it exceeds the per-entry cap.");
    }

    [TestMethod]
    public void ValueAtPerEntryCap_NotTruncated()
    {
        var atCapValue = new string('y', 100);
        var entries = new[] { Entry("subagent/x/y", atCapValue) };

        var section = DreamService.BuildSubagentWhiteboardSection(entries, perEntryCap: 100, maxEntries: 50);

        Assert.IsNotNull(section);
        Assert.IsFalse(section.Contains("…[truncated]"),
            "Boundary case: value exactly at cap should not be flagged as truncated.");
        StringAssert.Contains(section, atCapValue);
    }

    [TestMethod]
    public void NewerEntriesEmittedFirst_OldestFiltered_WhenOverMaxEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            Entry("subagent/old1/x", "old-value-1",  storedAt: now.AddHours(-3)),
            Entry("subagent/old2/x", "old-value-2",  storedAt: now.AddHours(-2)),
            Entry("subagent/new/x",  "newest-value", storedAt: now)
        };

        var section = DreamService.BuildSubagentWhiteboardSection(
            entries, perEntryCap: 1000, maxEntries: 2);

        Assert.IsNotNull(section);
        StringAssert.Contains(section, "newest-value");
        StringAssert.Contains(section, "old-value-2");
        Assert.IsFalse(section.Contains("old-value-1"),
            "When over maxEntries, oldest entries are dropped first.");

        // Order check: newest entry's key must appear before older entry's key
        var newestIdx = section.IndexOf("subagent/new/x", StringComparison.Ordinal);
        var olderIdx = section.IndexOf("subagent/old2/x", StringComparison.Ordinal);
        Assert.IsTrue(newestIdx >= 0 && olderIdx >= 0);
        Assert.IsTrue(newestIdx < olderIdx,
            "Section must list newest-first so size caps drop the oldest content.");
    }

    [TestMethod]
    public void MaxEntriesZero_ReturnsHeaderOnly_NoEntries()
    {
        var entries = new[] { Entry("subagent/x/y", "value") };

        var section = DreamService.BuildSubagentWhiteboardSection(entries, 1000, maxEntries: 0);

        Assert.IsNotNull(section);
        StringAssert.Contains(section, "Subagent verified data");
        Assert.IsFalse(section.Contains("[subagent/x/y]"),
            "maxEntries=0 should drop all entries while still emitting the section header.");
    }
}
