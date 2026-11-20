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
        StringAssert.Contains(message, "Do NOT call tools to fetch this same data again");
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
        StringAssert.Contains(message, "### wisp/w1/a/output\nsmall".Replace("\n", Environment.NewLine));
        StringAssert.Contains(message, "### wisp/w1/b/output — ");
        StringAssert.Contains(message, "chunk(s)");
        Assert.IsFalse(message.Contains(big), "An oversize value is not inlined.");
        var chunkKeys = memory.Values.Keys.Where(k => k.StartsWith("worker/t1/input-1-chunk", StringComparison.Ordinal)).ToList();
        Assert.IsTrue(chunkKeys.Count >= 2, "The value is chunked into the worker's namespace.");
        foreach (var key in chunkKeys)
            StringAssert.Contains(message, $"`{key}`");
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

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string value, TimeSpan? ttl = null,
            string? category = null, IReadOnlyList<string>? tags = null)
        {
            Values[key] = value;
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
