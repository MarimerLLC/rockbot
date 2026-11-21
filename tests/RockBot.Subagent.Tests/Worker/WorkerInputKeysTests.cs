using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Subagent.Worker;

namespace RockBot.Subagent.Tests.Worker;

/// <summary>
/// #847: a worker takes data references (<c>input_keys</c>) so it filters a wisp's output
/// instead of re-running the wisp's fetches.
/// </summary>
[TestClass]
public class WorkerInputKeysTests
{
    [TestMethod]
    [DataRow("wisp:wisp-1234/search", "wisp/wisp-1234/search/output")]
    [DataRow("wisp:wisp-1234/search/output", "wisp/wisp-1234/search/output")]
    [DataRow("wisp:w1/output", "wisp/w1/output/output")]
    [DataRow("wisp:wisp/w1/output", "wisp/w1/output/output")]
    [DataRow("wisp:w1/output/output", "wisp/w1/output/output")]
    [DataRow("wisp:wisp/wisp-1234/search", "wisp/wisp-1234/search/output")]
    [DataRow("WISP: wisp-1234/search ", "wisp/wisp-1234/search/output")]
    [DataRow("wisp/wisp-1234/search/output", "wisp/wisp-1234/search/output")]
    [DataRow("subagent/abc/emails", "subagent/abc/emails")]
    [DataRow(" shared/patrol/calendar ", "shared/patrol/calendar")]
    public void ResolveInputKey_ResolvesReferencesAndFullKeys(string entry, string expected) =>
        Assert.AreEqual(expected, WorkerDefinition.ResolveInputKey(entry));

    [TestMethod]
    [DataRow("email-raw")]
    [DataRow("wisp:wisp-1234")]
    [DataRow("wisp:")]
    [DataRow("")]
    [DataRow("   ")]
    public void ResolveInputKey_RefusesBareAndIncompleteKeys(string entry) =>
        Assert.IsNull(WorkerDefinition.ResolveInputKey(entry));

    [TestMethod]
    public void InputKeys_Deserializes()
    {
        var def = JsonSerializer.Deserialize<WorkerDefinition>(
            """{"description":"filter","input_keys":["wisp:w1/s1","shared/x"]}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        CollectionAssert.AreEqual(new[] { "wisp:w1/s1", "shared/x" }, def!.InputKeys!.ToArray());
    }

    [TestMethod]
    public async Task BuildInputDataMessage_NoKeys_ReturnsNull()
    {
        Assert.IsNull(await WorkerRunner.BuildInputDataMessageAsync(
            null, new FakeWorkingMemory(), "worker/t1", 1000, NullLogger.Instance));
        Assert.IsNull(await WorkerRunner.BuildInputDataMessageAsync(
            [], new FakeWorkingMemory(), "worker/t1", 1000, NullLogger.Instance));
    }

    [TestMethod]
    public async Task BuildInputDataMessage_InlinesSmallValues_AndSaysNotToRefetch()
    {
        var memory = new FakeWorkingMemory();
        memory.Values["wisp/w1/search/output"] = "[{\"subject\":\"Invoice\"}]";

        var message = await WorkerRunner.BuildInputDataMessageAsync(
            ["wisp:w1/search"], memory, "worker/t1", 1000, NullLogger.Instance);

        Assert.IsNotNull(message);
        StringAssert.Contains(message, "already fetched");
        StringAssert.Contains(message, "Do NOT call");
        StringAssert.Contains(message, "fetch this same data again");
        StringAssert.Contains(message, "### wisp/w1/search/output");
        StringAssert.Contains(message, "\"subject\":\"Invoice\"");
    }

    [TestMethod]
    public async Task BuildInputDataMessage_ChunksValuesOverTheBudget()
    {
        var memory = new FakeWorkingMemory();
        memory.Values["wisp/w1/a/output"] = "small";
        var big = string.Join("\n\n", Enumerable.Range(0, 400).Select(i => $"Paragraph {i}: " + new string('x', 100)));
        memory.Values["wisp/w1/b/output"] = big;

        var message = await WorkerRunner.BuildInputDataMessageAsync(
            ["wisp:w1/a", "wisp:w1/b"], memory, "worker/t1", 1000, NullLogger.Instance);

        Assert.IsNotNull(message);
        StringAssert.Contains(message, "### wisp/w1/a/output");
        StringAssert.Contains(message, $"{Environment.NewLine}small");
        StringAssert.Contains(message, "### wisp/w1/b/output — ");
        StringAssert.Contains(message, "chunk(s)");
        Assert.IsFalse(message.Contains(big), "An oversize value is not inlined.");
        var chunkKeys = memory.Values.Keys.Where(k => k.StartsWith("worker/t1/input-1-chunk", StringComparison.Ordinal)).ToList();
        Assert.IsTrue(chunkKeys.Count >= 2, "The value is chunked into the worker's namespace.");
        foreach (var key in chunkKeys)
            StringAssert.Contains(message, $"`{key}`");
    }

    [TestMethod]
    public async Task BuildInputDataMessage_ChunkCopiesUseARegenerableCategory()
    {
        var memory = new FakeWorkingMemory();
        memory.Values["wisp/w1/b/output"] = string.Join("\n\n", Enumerable.Range(0, 400).Select(i => $"Paragraph {i}: " + new string('x', 100)));

        await WorkerRunner.BuildInputDataMessageAsync(["wisp:w1/b"], memory, "worker/t1", 1000, NullLogger.Instance);

        var categories = memory.Categories
            .Where(c => c.Key.StartsWith("worker/t1/input-", StringComparison.Ordinal))
            .Select(c => c.Value)
            .Distinct()
            .ToList();
        CollectionAssert.AreEqual(new[] { WorkerRunner.InputChunkCategory }, categories);
        CollectionAssert.Contains(WorkingMemoryOptions.DefaultEvictableCategories.ToList(), WorkerRunner.InputChunkCategory,
            "Chunk copies must not use up the saved-notes budget the worker needs for its result key.");
    }

    [TestMethod]
    public async Task BuildInputDataMessage_StaleSnapshot_KeepsTheStaleWarning()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var memory = new FakeWorkingMemory();
        memory.Entries["shared/patrol/calendar-latest"] = new WorkingMemoryEntry(
            "shared/patrol/calendar-latest", "{\"events\":[]}", now.AddDays(-2), now.AddDays(1),
            Writer: "patrol/heartbeat-patrol");

        var message = await WorkerRunner.BuildInputDataMessageAsync(
            ["shared/patrol/calendar-latest"], memory, "worker/t1", 1000, NullLogger.Instance,
            new WorkingMemoryOptions(), now);

        Assert.IsNotNull(message);
        StringAssert.Contains(message, "[STALE SNAPSHOT — written by patrol/heartbeat-patrol");
        StringAssert.Contains(message, "it overrides the no-refetch rule for that key");
        Assert.IsFalse(message.Contains("complete and current"), "A snapshot is never declared current.");
    }

    [TestMethod]
    public async Task BuildInputDataMessage_OrdinaryKey_ShowsWhoWroteItAndWhen()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var memory = new FakeWorkingMemory();
        memory.Entries["wisp/w1/s/output"] = new WorkingMemoryEntry(
            "wisp/w1/s/output", "[]", now.AddMinutes(-5), now.AddMinutes(25), Writer: "wisp/w1");

        var message = await WorkerRunner.BuildInputDataMessageAsync(
            ["wisp:w1/s"], memory, "worker/t1", 1000, NullLogger.Instance, new WorkingMemoryOptions(), now);

        StringAssert.Contains(message!, "[stored 2026-11-20T11:55Z (5 minutes ago) by wisp/w1]");
    }

    [TestMethod]
    public async Task BuildInputDataMessage_MissingKey_IsNamedNotRefetched()
    {
        var message = await WorkerRunner.BuildInputDataMessageAsync(
            ["shared/gone"], new FakeWorkingMemory(), "worker/t1", 1000, NullLogger.Instance);

        Assert.IsNotNull(message);
        StringAssert.Contains(message, "### shared/gone — MISSING");
        StringAssert.Contains(message, "do not refetch");
    }

    private sealed class FakeWorkingMemory : IWorkingMemory
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> Categories { get; } = new(StringComparer.Ordinal);

        /// <summary>Entries with metadata, for the provenance header; also readable by value.</summary>
        public Dictionary<string, WorkingMemoryEntry> Entries { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(Values.TryGetValue(key, out var v) ? v
                : Entries.TryGetValue(key, out var e) ? e.Value : null);

        public Task<WorkingMemoryEntry?> GetEntryAsync(string key) =>
            Task.FromResult(Entries.TryGetValue(key, out var e) ? e : null);

        public Task SetAsync(string key, string value, TimeSpan? ttl = null,
            string? category = null, IReadOnlyList<string>? tags = null)
        {
            Values[key] = value;
            Categories[key] = category;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

        public Task DeleteAsync(string key) => Task.CompletedTask;

        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(
            MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }
}
