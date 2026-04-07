using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Memory;

namespace RockBot.Memory.Tests;

[TestClass]
public class WorkingMemoryToolsTests
{
    private const string Namespace = "subagent/abc123";
    private StubWorkingMemory _memory = null!;
    private WorkingMemoryTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _memory = new StubWorkingMemory();
        _tools = new WorkingMemoryTools(_memory, Namespace, NullLogger.Instance);
    }

    // ── SaveToWorkingMemory ───────────────────────────────────────────────

    [TestMethod]
    public async Task SaveToWorkingMemory_PlainKey_PrependsNamespace()
    {
        await _tools.SaveToWorkingMemory("my_results", "data");

        Assert.IsTrue(_memory.Store.ContainsKey("subagent/abc123/my_results"));
    }

    [TestMethod]
    public async Task SaveToWorkingMemory_AbsoluteKey_DoesNotDoublePrefix()
    {
        await _tools.SaveToWorkingMemory("subagent/abc123/my_results", "data");

        Assert.IsTrue(_memory.Store.ContainsKey("subagent/abc123/my_results"),
            "Key should be used as-is when it contains '/'");
        Assert.IsFalse(_memory.Store.ContainsKey("subagent/abc123/subagent/abc123/my_results"),
            "Namespace must not be prepended twice");
    }

    // ── GetFromWorkingMemory ──────────────────────────────────────────────

    [TestMethod]
    public async Task GetFromWorkingMemory_PlainKey_PrependsNamespace()
    {
        _memory.Store["subagent/abc123/cached"] = "value";

        var result = await _tools.GetFromWorkingMemory("cached");

        Assert.AreEqual("value", result);
    }

    [TestMethod]
    public async Task GetFromWorkingMemory_AbsoluteKey_UsesAsIs()
    {
        _memory.Store["patrol/heartbeat/alert"] = "alert-data";

        var result = await _tools.GetFromWorkingMemory("patrol/heartbeat/alert");

        Assert.AreEqual("alert-data", result);
    }

    // ── DeleteFromWorkingMemory ───────────────────────────────────────────

    [TestMethod]
    public async Task DeleteFromWorkingMemory_PlainKey_PrependsNamespace()
    {
        _memory.Store["subagent/abc123/old"] = "stale";

        await _tools.DeleteFromWorkingMemory("old");

        Assert.IsFalse(_memory.Store.ContainsKey("subagent/abc123/old"));
    }

    [TestMethod]
    public async Task DeleteFromWorkingMemory_AbsoluteKey_UsesAsIs()
    {
        _memory.Store["patrol/heartbeat/alert"] = "alert-data";

        await _tools.DeleteFromWorkingMemory("patrol/heartbeat/alert");

        Assert.IsFalse(_memory.Store.ContainsKey("patrol/heartbeat/alert"));
    }

    // ── Stub ──────────────────────────────────────────────────────────────

    private sealed class StubWorkingMemory : IWorkingMemory
    {
        public Dictionary<string, string> Store { get; } = new();

        public Task SetAsync(string key, string value, TimeSpan? ttl = null,
            string? category = null, IReadOnlyList<string>? tags = null)
        {
            Store[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(Store.TryGetValue(key, out var v) ? v : null);

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

        public Task DeleteAsync(string key)
        {
            Store.Remove(key);
            return Task.CompletedTask;
        }

        public Task ClearAsync(string? prefix = null)
        {
            Store.Clear();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }
}
