using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

[TestClass]
public class WorkingMemoryEvictApplierTests
{
    [TestMethod]
    public async Task Apply_KeyPrefix_EvictsMatchingEntries_LeavesOthers()
    {
        var wm = new InMemoryWorkingMemory();
        await wm.SetAsync("claim/capability/calendar/x", "v1");
        await wm.SetAsync("claim/capability/calendar/y", "v2");
        await wm.SetAsync("session/abc/scratch", "keep me");

        var applier = NewApplier(wm);
        var ticket = NewTicket("""{ "keyPrefix": "claim/capability/calendar/" }""");

        var outcome = await applier.ApplyAsync(ticket, CancellationToken.None);

        Assert.IsNull(await wm.GetAsync("claim/capability/calendar/x"));
        Assert.IsNull(await wm.GetAsync("claim/capability/calendar/y"));
        Assert.AreEqual("keep me", await wm.GetAsync("session/abc/scratch"));
        Assert.AreEqual(2, outcome.AppliedDiff.GetProperty("evictedCount").GetInt32());
        Assert.IsNull(outcome.Revert);
    }

    [TestMethod]
    public async Task Apply_KeysList_EvictsExactKeys()
    {
        var wm = new InMemoryWorkingMemory();
        await wm.SetAsync("a", "1");
        await wm.SetAsync("b", "2");
        await wm.SetAsync("c", "3");

        var applier = NewApplier(wm);
        var ticket = NewTicket("""{ "keys": ["a", "c"] }""");

        await applier.ApplyAsync(ticket, CancellationToken.None);

        Assert.IsNull(await wm.GetAsync("a"));
        Assert.AreEqual("2", await wm.GetAsync("b"));
        Assert.IsNull(await wm.GetAsync("c"));
    }

    [TestMethod]
    public async Task Apply_KeyPrefix_NoMatches_IsIdempotent()
    {
        var wm = new InMemoryWorkingMemory();
        var applier = NewApplier(wm);
        var ticket = NewTicket("""{ "keyPrefix": "no/such/prefix" }""");

        var outcome = await applier.ApplyAsync(ticket, CancellationToken.None);

        Assert.AreEqual(0, outcome.AppliedDiff.GetProperty("evictedCount").GetInt32());
    }

    [TestMethod]
    public async Task Apply_NeitherPrefixNorKeys_Throws()
    {
        var wm = new InMemoryWorkingMemory();
        var applier = NewApplier(wm);
        var ticket = NewTicket("""{}""");

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => applier.ApplyAsync(ticket, CancellationToken.None));
    }

    private static WorkingMemoryEvictApplier NewApplier(IWorkingMemory wm) =>
        new(wm, NullLogger<WorkingMemoryEvictApplier>.Instance);

    private static RepairTicket NewTicket(string changeJson) =>
        new(
            Id: "t-1",
            PatternKey: "p|q|r",
            Target: RepairTarget.WorkingMemoryEvict,
            Change: JsonDocument.Parse(changeJson).RootElement,
            Verify: new VerifyShape("svr", "tool", JsonDocument.Parse("{}").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success)),
            Attempts: [],
            Status: RepairStatus.Open,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    internal sealed class InMemoryWorkingMemory : IWorkingMemory
    {
        private readonly Dictionary<string, WorkingMemoryEntry> _entries = new(StringComparer.Ordinal);

        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null)
        {
            var now = DateTimeOffset.UtcNow;
            var expires = now.Add(ttl ?? TimeSpan.FromHours(1));
            _entries[key] = new WorkingMemoryEntry(key, value, now, expires, category, tags);
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(_entries.TryGetValue(key, out var e) ? e.Value : null);

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null)
        {
            IReadOnlyList<WorkingMemoryEntry> list = _entries.Values
                .Where(e => string.IsNullOrEmpty(prefix) || e.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            return Task.FromResult(list);
        }

        public Task DeleteAsync(string key)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }

        public Task ClearAsync(string? prefix = null)
        {
            var toRemove = _entries.Keys
                .Where(k => string.IsNullOrEmpty(prefix) || k.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            foreach (var k in toRemove)
                _entries.Remove(k);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            ListAsync(prefix);
    }
}
