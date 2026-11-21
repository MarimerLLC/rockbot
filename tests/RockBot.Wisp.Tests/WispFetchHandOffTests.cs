using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Wisp.Tests;

/// <summary>
/// #847: a wisp's fetches are recorded in the parent run's fetch registry, and its outputs stay in
/// working memory long enough to hand to a worker.
/// </summary>
[TestClass]
public class WispFetchHandOffTests
{
    private static WispDefinition SearchWisp() => new()
    {
        Description = "Search",
        Steps =
        [
            new WispStep
            {
                Id = "search",
                Mode = StepMode.Direct,
                Gateway = GatewayType.Web,
                Tool = "web_search",
                Params = JsonDocument.Parse("""{"query": "invoices", "count": 5}""").RootElement,
            }
        ]
    };

    private static (WispExecutor Executor, TtlRecordingWorkingMemory Memory) Create(WispOptions? options = null)
    {
        var registry = new FakeToolRegistry();
        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("3 results"));
        var memory = new TtlRecordingWorkingMemory();
        var executor = new WispExecutor(registry, memory, agentLoopRunner: null!, options ?? new WispOptions(),
            NullLogger<WispExecutor>.Instance);
        return (executor, memory);
    }

    [TestMethod]
    public async Task DirectStep_IsRecordedInTheParentRunsFetchRegistry()
    {
        var (executor, _) = Create();
        var fetches = new RunFetchRegistry();
        using var scope = RunFetchRegistryContext.Set(fetches);

        var result = await executor.ExecuteAsync(SearchWisp(), "wisp-1234", CancellationToken.None);
        Assert.IsTrue(result.IsSuccess);

        // The parent (or a worker it spawns) repeating the same search is told where the data is.
        var prior = fetches.Observe("web_search",
            new Dictionary<string, object?> { ["count"] = 5, ["query"] = "invoices" }, true, "by an earlier direct call");
        Assert.IsNotNull(prior);
        Assert.AreEqual("by wisp wisp-1234 step search", prior.Location);
        Assert.AreEqual("wisp/wisp-1234/search/output", prior.Key);
    }

    [TestMethod]
    public async Task DirectStep_WithoutARegistry_StillRuns()
    {
        var (executor, _) = Create();
        using var scope = RunFetchRegistryContext.Set(null);

        var result = await executor.ExecuteAsync(SearchWisp(), "wisp-1234", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task SuccessfulWisp_KeepsOutputsForTheConfiguredTtl()
    {
        var (executor, memory) = Create(new WispOptions { SuccessOutputTtl = TimeSpan.FromMinutes(45) });

        var result = await executor.ExecuteAsync(SearchWisp(), "wisp-1234", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(TimeSpan.FromMinutes(45), memory.Ttls["wisp/wisp-1234/search/output"]);
    }

    [TestMethod]
    public void SuccessOutputTtl_DefaultsToThirtyMinutes() =>
        Assert.AreEqual(TimeSpan.FromMinutes(30), new WispOptions().SuccessOutputTtl);

    /// <summary>Working memory that lists what it stores and remembers each key's last TTL.</summary>
    private sealed class TtlRecordingWorkingMemory : IWorkingMemory
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);
        public Dictionary<string, TimeSpan?> Ttls { get; } = new(StringComparer.Ordinal);

        public Task SetAsync(string key, string value, TimeSpan? ttl = null,
            string? category = null, IReadOnlyList<string>? tags = null)
        {
            _store[key] = value;
            Ttls[key] = ttl;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>(_store
                .Where(e => prefix is null || e.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(e => new WorkingMemoryEntry(e.Key, e.Value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)))
                .ToList());

        public Task DeleteAsync(string key)
        {
            _store.Remove(key);
            return Task.CompletedTask;
        }

        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(
            MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }
}
