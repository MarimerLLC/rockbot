namespace RockBot.Host.Tests;

[TestClass]
public class ToolSuccessLearningHelpersTests
{
    private static DreamService.ToolRetryPattern Pattern(
        string sessionId = "s1",
        string toolName = "list_files",
        IReadOnlyList<string>? failedArgs = null,
        string successArgs = """{"server":"onedrive-personal"}""",
        DateTimeOffset? lastSeenAt = null) =>
        new(
            sessionId,
            toolName,
            failedArgs ?? ["""{"server":"onedrive-marimer"}"""],
            successArgs,
            lastSeenAt ?? DateTimeOffset.UtcNow);

    // ── DedupeRetryPatterns ──────────────────────────────────────────────────────

    [TestMethod]
    public void Dedupe_SameToolAndSuccess_KeepsNewestOccurrence()
    {
        var older = Pattern(sessionId: "old-session",
            lastSeenAt: DateTimeOffset.UtcNow.AddDays(-3));
        var newer = Pattern(sessionId: "new-session",
            lastSeenAt: DateTimeOffset.UtcNow);

        var deduped = DreamService.DedupeRetryPatterns([older, newer], maxCount: 50);

        Assert.AreEqual(1, deduped.Count);
        Assert.AreEqual("new-session", deduped[0].SessionId,
            "Deduplication should retain the most recent occurrence so the lesson is fresh.");
    }

    [TestMethod]
    public void Dedupe_DifferentSuccessArgs_KeepsBoth()
    {
        var a = Pattern(toolName: "list_files", successArgs: """{"server":"onedrive-personal"}""");
        var b = Pattern(toolName: "list_files", successArgs: """{"server":"onedrive-work"}""");

        var deduped = DreamService.DedupeRetryPatterns([a, b], maxCount: 50);

        Assert.AreEqual(2, deduped.Count, "Different verified values are different lessons.");
    }

    [TestMethod]
    public void Dedupe_DifferentTools_KeepsBoth()
    {
        var a = Pattern(toolName: "tool-X", successArgs: """{"v":1}""");
        var b = Pattern(toolName: "tool-Y", successArgs: """{"v":1}""");

        var deduped = DreamService.DedupeRetryPatterns([a, b], maxCount: 50);

        Assert.AreEqual(2, deduped.Count);
    }

    [TestMethod]
    public void Dedupe_HonorsMaxCount()
    {
        var patterns = Enumerable.Range(0, 100)
            .Select(i => Pattern(toolName: $"tool-{i}", successArgs: $"args-{i}"))
            .ToArray();

        var deduped = DreamService.DedupeRetryPatterns(patterns, maxCount: 25);

        Assert.AreEqual(25, deduped.Count);
    }

    // ── BuildToolSuccessLearningUserMessage ──────────────────────────────────────

    [TestMethod]
    public void BuildMessage_IncludesAllPatternsAsNumberedSections()
    {
        var patterns = new[]
        {
            Pattern(toolName: "list_files",
                failedArgs: ["""{"server":"onedrive-marimer"}"""],
                successArgs: """{"server":"onedrive-personal"}"""),
            Pattern(toolName: "get_calendar_events",
                failedArgs: ["{}"],
                successArgs: """{"accountId":"xebia"}""")
        };

        var msg = DreamService.BuildToolSuccessLearningUserMessage(patterns);

        StringAssert.Contains(msg, "### Pattern 1: tool 'list_files'");
        StringAssert.Contains(msg, "### Pattern 2: tool 'get_calendar_events'");
        StringAssert.Contains(msg, "onedrive-personal");
        StringAssert.Contains(msg, """{"accountId":"xebia"}""");
    }

    [TestMethod]
    public void BuildMessage_FailedArgsJoinedWithPipe()
    {
        var patterns = new[]
        {
            Pattern(failedArgs: ["""{"x":1}""", """{"x":2}""", """{"x":3}"""])
        };

        var msg = DreamService.BuildToolSuccessLearningUserMessage(patterns);

        StringAssert.Contains(msg, """{"x":1} | {"x":2} | {"x":3}""");
    }

    [TestMethod]
    public void BuildMessage_EmptyPatterns_StillProducesInstructionalHeader()
    {
        var msg = DreamService.BuildToolSuccessLearningUserMessage([]);

        StringAssert.Contains(msg, "retry-until-success",
            "Header instructional text should always appear so the directive's expectations are clear.");
        Assert.IsFalse(msg.Contains("### Pattern"),
            "No patterns means no pattern sections.");
    }

    // ── NormalizeToolSuccessLearningEntries ──────────────────────────────────────

    [TestMethod]
    public void Normalize_AddsCanonicalTags_WhenMissing()
    {
        var dto = new DreamService.MemoryMiningResultDto(
        [
            new DreamService.MemoryMiningEntryDto(
                Content: "Teams bridge JSON lives on onedrive-personal at /Apps/RockBot/xebia-teams.",
                Category: null,
                Tags: null)
        ]);

        var entries = DreamService.NormalizeToolSuccessLearningEntries(
            dto, idFactory: () => "id-1", nowFactory: () => DateTimeOffset.UnixEpoch);

        Assert.AreEqual(1, entries.Count);
        CollectionAssert.Contains(entries[0].Tags.ToArray(), "verified");
        CollectionAssert.Contains(entries[0].Tags.ToArray(), "tool-success-learned");
    }

    [TestMethod]
    public void Normalize_DoesNotDuplicateExistingCanonicalTags()
    {
        var dto = new DreamService.MemoryMiningResultDto(
        [
            new DreamService.MemoryMiningEntryDto(
                Content: "fact",
                Category: "tool-knowledge/onedrive",
                Tags: ["verified", "tool-success-learned", "extra"])
        ]);

        var entries = DreamService.NormalizeToolSuccessLearningEntries(
            dto, () => "id-1", () => DateTimeOffset.UnixEpoch);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(3, entries[0].Tags.Count,
            "Already-present canonical tags should not be duplicated.");
    }

    [TestMethod]
    public void Normalize_CanonicalTagMatchIsCaseInsensitive()
    {
        var dto = new DreamService.MemoryMiningResultDto(
        [
            new DreamService.MemoryMiningEntryDto("fact", null, ["VERIFIED", "Tool-Success-Learned"])
        ]);

        var entries = DreamService.NormalizeToolSuccessLearningEntries(
            dto, () => "id-1", () => DateTimeOffset.UnixEpoch);

        Assert.AreEqual(2, entries[0].Tags.Count,
            "Tag matching for canonical-tag presence must be case-insensitive.");
    }

    [TestMethod]
    public void Normalize_DefaultsBlankCategory_ToToolKnowledge()
    {
        var dto = new DreamService.MemoryMiningResultDto(
        [
            new DreamService.MemoryMiningEntryDto("fact", "  ", null),
            new DreamService.MemoryMiningEntryDto("fact-2", null, null),
            new DreamService.MemoryMiningEntryDto("fact-3", "", null)
        ]);

        var entries = DreamService.NormalizeToolSuccessLearningEntries(
            dto, () => "id", () => DateTimeOffset.UnixEpoch);

        Assert.IsTrue(entries.All(e => e.Category == "tool-knowledge"));
    }

    [TestMethod]
    public void Normalize_PreservesSuppliedCategory_AfterTrimming()
    {
        var dto = new DreamService.MemoryMiningResultDto(
        [
            new DreamService.MemoryMiningEntryDto("fact", "  tool-knowledge/onedrive  ", null)
        ]);

        var entries = DreamService.NormalizeToolSuccessLearningEntries(
            dto, () => "id", () => DateTimeOffset.UnixEpoch);

        Assert.AreEqual("tool-knowledge/onedrive", entries[0].Category);
    }

    [TestMethod]
    public void Normalize_SkipsEmptyContent()
    {
        var dto = new DreamService.MemoryMiningResultDto(
        [
            new DreamService.MemoryMiningEntryDto("", null, null),
            new DreamService.MemoryMiningEntryDto("   ", null, null),
            new DreamService.MemoryMiningEntryDto("real fact", null, null)
        ]);

        var entries = DreamService.NormalizeToolSuccessLearningEntries(
            dto, () => "id", () => DateTimeOffset.UnixEpoch);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("real fact", entries[0].Content);
    }

    [TestMethod]
    public void Normalize_TrimsContent()
    {
        var dto = new DreamService.MemoryMiningResultDto(
        [
            new DreamService.MemoryMiningEntryDto("  fact with whitespace  \n", null, null)
        ]);

        var entries = DreamService.NormalizeToolSuccessLearningEntries(
            dto, () => "id", () => DateTimeOffset.UnixEpoch);

        Assert.AreEqual("fact with whitespace", entries[0].Content);
    }

    [TestMethod]
    public void Normalize_NullDto_ReturnsEmpty()
    {
        var entries = DreamService.NormalizeToolSuccessLearningEntries(
            null, () => "id", () => DateTimeOffset.UnixEpoch);

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public void Normalize_NullToSave_ReturnsEmpty()
    {
        var dto = new DreamService.MemoryMiningResultDto(ToSave: null);

        var entries = DreamService.NormalizeToolSuccessLearningEntries(
            dto, () => "id", () => DateTimeOffset.UnixEpoch);

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public void Normalize_AssignsConsistentTimestampsAndIds()
    {
        var fixedTime = DateTimeOffset.Parse("2026-04-27T10:00:00Z");
        var ids = new[] { "id-A", "id-B", "id-C" };
        var idIndex = 0;

        var dto = new DreamService.MemoryMiningResultDto(
        [
            new DreamService.MemoryMiningEntryDto("fact-1", null, null),
            new DreamService.MemoryMiningEntryDto("fact-2", null, null),
            new DreamService.MemoryMiningEntryDto("fact-3", null, null)
        ]);

        var entries = DreamService.NormalizeToolSuccessLearningEntries(
            dto,
            idFactory: () => ids[idIndex++],
            nowFactory: () => fixedTime);

        CollectionAssert.AreEqual(ids, entries.Select(e => e.Id).ToArray(),
            "Each entry must call the id factory once, in order.");
        Assert.IsTrue(entries.All(e => e.CreatedAt == fixedTime && e.UpdatedAt == fixedTime));
    }

    // ── FormatToolRetryNote ──────────────────────────────────────────────────────

    [TestMethod]
    public void FormatRetryNote_RendersSingleFailedArg()
    {
        var p = Pattern(
            toolName: "list_files",
            failedArgs: ["""{"server":"onedrive-marimer"}"""],
            successArgs: """{"server":"onedrive-personal"}""");

        var note = DreamService.FormatToolRetryNote(p);

        StringAssert.Contains(note, "list_files");
        StringAssert.Contains(note, "onedrive-marimer");
        StringAssert.Contains(note, "onedrive-personal");
        StringAssert.Contains(note, "then succeeded");
    }

    [TestMethod]
    public void FormatRetryNote_RendersMultipleFailedArgs_PipeSeparated()
    {
        var p = Pattern(failedArgs: ["a", "b", "c"], successArgs: "d");

        var note = DreamService.FormatToolRetryNote(p);

        StringAssert.Contains(note, "[a | b | c]");
        StringAssert.Contains(note, "[d]");
    }

    // ── GroupRetryPatternsBySession ──────────────────────────────────────────────

    [TestMethod]
    public void GroupBySession_GroupsMultiplePatternsForSameSession()
    {
        var patterns = new[]
        {
            Pattern(sessionId: "session-A", toolName: "tool-1"),
            Pattern(sessionId: "session-A", toolName: "tool-2"),
            Pattern(sessionId: "session-B", toolName: "tool-3")
        };

        var grouped = DreamService.GroupRetryPatternsBySession(patterns);

        Assert.AreEqual(2, grouped.Count);
        Assert.AreEqual(2, grouped["session-A"].Count);
        Assert.AreEqual(1, grouped["session-B"].Count);
    }

    [TestMethod]
    public void GroupBySession_SessionIdLookupIsCaseInsensitive()
    {
        var patterns = new[] { Pattern(sessionId: "Session-A") };

        var grouped = DreamService.GroupRetryPatternsBySession(patterns);

        Assert.IsTrue(grouped.ContainsKey("session-a"),
            "Session IDs should match case-insensitively for lookup convenience.");
    }

    [TestMethod]
    public void GroupBySession_EmptyInput_ReturnsEmptyDictionary()
    {
        var grouped = DreamService.GroupRetryPatternsBySession([]);
        Assert.AreEqual(0, grouped.Count);
    }
}
