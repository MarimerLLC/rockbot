using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Memory;

namespace RockBot.Memory.Tests;

/// <summary><c>save_to_working_memory</c> against a full namespace (#801).</summary>
[TestClass]
public sealed class WorkingMemoryToolsFullNamespaceTests
{
    [TestMethod]
    public async Task Save_RefusedByAFullNamespace_SaysItWasNotSaved()
    {
        var memory = new ScriptedWorkingMemory(WorkingMemoryWriteResult.Refused("working memory for 'subagent/abc123' is full (50 entries)"));
        var tools = new WorkingMemoryTools(memory, "subagent/abc123", NullLogger.Instance);

        var result = await tools.SaveToWorkingMemory("findings", "the data");

        StringAssert.StartsWith(result, "Error: NOT saved");
        StringAssert.Contains(result, "is full");
        StringAssert.Contains(result, "delete_from_working_memory");
        Assert.IsFalse(result.Contains("Saved to working memory"));
    }

    [TestMethod]
    public async Task Save_ThatEvictedCachedChunks_SaysSo()
    {
        var memory = new ScriptedWorkingMemory(new WorkingMemoryWriteResult(true, null,
            ["subagent/abc123/web-x-chunk0", "subagent/abc123/web-x-index"]));
        var tools = new WorkingMemoryTools(memory, "subagent/abc123", NullLogger.Instance);

        var result = await tools.SaveToWorkingMemory("findings", "the data");

        StringAssert.StartsWith(result, "Saved to working memory under key 'subagent/abc123/findings'.");
        StringAssert.Contains(result, "2 older cached page/result chunk(s) were evicted");
    }

    /// <summary>Answers every write with the given result.</summary>
    private sealed class ScriptedWorkingMemory(WorkingMemoryWriteResult result) : IWorkingMemory
    {
        public Task SetAsync(string key, string value, TimeSpan? ttl = null,
            string? category = null, IReadOnlyList<string>? tags = null) => Task.CompletedTask;

        public Task<WorkingMemoryWriteResult> TrySetAsync(string key, string value, TimeSpan? ttl = null,
            string? category = null, IReadOnlyList<string>? tags = null, string? writer = null) => Task.FromResult(result);

        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }
}
