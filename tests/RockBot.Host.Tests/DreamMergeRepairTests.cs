using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers the second chance a merge gets after the coverage check rejects it.
/// </summary>
/// <remarks>
/// Rejection alone left the duplicate cluster in place, so the next cycle proposed the same merge
/// and the check rejected it the same way — a live corpus rejected one six-source merge five times
/// in eight cycles. The repair call is a narrower task than merging: the model is handed the exact
/// strings it dropped and asked to put them back, and its answer faces the same check, so a bad
/// repair lands exactly where the rejection would have.
/// </remarks>
[TestClass]
public class DreamMergeRepairTests
{
    private const string SourceA = "Rockford Duane Lhotka uses timezone America/Chicago.";
    private const string SourceB = "Accounts span Microsoft and Marimer LLC.";
    private const string LossyMerge = "The user has accounts across providers and a default timezone.";
    private const string RepairedMerge =
        "Rockford Duane Lhotka uses timezone America/Chicago; accounts span Microsoft and Marimer LLC.";

    [TestMethod]
    public async Task ARejectedMerge_IsRepairedAndApplied()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("a", SourceA));
        await memory.SaveAsync(Entry("b", SourceB));

        var llm = new ScriptedLlmClient(
            ConsolidationResponse(LossyMerge),
            $$$"""{"content": "{{{RepairedMerge}}}"}""");

        var service = CreateService(memory, new DreamOptions { Enabled = false }, llm);

        var (deleted, saved) = await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.AreEqual(2, llm.Calls.Count, "The rejection should have triggered exactly one repair call.");
        StringAssert.Contains(llm.Calls[1], "America/Chicago",
            "The repair prompt must name the specifics the merge dropped.");

        Assert.AreEqual(1, saved);
        Assert.AreEqual(2, deleted);

        CollectionAssert.AreEquivalent(new[] { "a", "b" }, memory.Archived.Select(x => x.Id).ToArray());

        var merged = memory.Snapshot().Single(e => e.Id is not "a" and not "b");
        Assert.AreEqual(RepairedMerge, merged.Content);
    }

    [TestMethod]
    public async Task ARepairThatStillDropsSpecifics_LeavesTheSourcesAlone()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("a", SourceA));
        await memory.SaveAsync(Entry("b", SourceB));

        var llm = new ScriptedLlmClient(
            ConsolidationResponse(LossyMerge),
            """{"content": "Rockford Duane Lhotka has accounts and a timezone."}""");

        var service = CreateService(memory, new DreamOptions { Enabled = false }, llm);

        var (deleted, saved) = await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.AreEqual(0, saved);
        Assert.AreEqual(0, deleted);
        Assert.AreEqual(0, memory.Archived.Count, "A merge that is still lossy after repair keeps its sources.");
        Assert.AreEqual(2, memory.Snapshot().Count);
    }

    [TestMethod]
    public async Task WithRepairDisabled_TheRejectionIsTerminalAndNoSecondCallIsMade()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("a", SourceA));
        await memory.SaveAsync(Entry("b", SourceB));

        var llm = new ScriptedLlmClient(ConsolidationResponse(LossyMerge));
        var service = CreateService(
            memory, new DreamOptions { Enabled = false, MergeRepairEnabled = false }, llm);

        var (deleted, saved) = await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.AreEqual(1, llm.Calls.Count);
        Assert.AreEqual(0, saved);
        Assert.AreEqual(0, deleted);
    }

    [TestMethod]
    public async Task RepairAttemptsAreCappedPerCycle()
    {
        // A cycle in which the model merges badly across the board must not turn into an
        // unbounded run of LLM calls.
        var memory = new ArchivingStore();
        for (var i = 0; i < 4; i++)
        {
            await memory.SaveAsync(Entry($"a{i}", $"Rockford Lhotka owns account {i}00 at Marimer LLC."));
            await memory.SaveAsync(Entry($"b{i}", $"Account {i}00 is administered from Austin."));
        }

        var merges = string.Join(",", Enumerable.Range(0, 4).Select(i =>
            $$"""{"content": "An account exists.", "sourceIds": ["a{{i}}", "b{{i}}"]}"""));

        var llm = new ScriptedLlmClient($$"""{"toDelete": [], "toSave": [{{merges}}]}""");
        var service = CreateService(
            memory,
            new DreamOptions { Enabled = false, MergeRepairMaxPerCycle = 2 },
            llm);

        await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.AreEqual(3, llm.Calls.Count,
            "One consolidation call plus the two repairs the cap allows.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ConsolidationResponse(string mergedContent) =>
        $$"""
        {
          "toDelete": ["a", "b"],
          "toSave": [{ "content": "{{mergedContent}}", "sourceIds": ["a", "b"] }]
        }
        """;

    private static MemoryEntry Entry(string id, string content) =>
        new(id, content, null, [], DateTimeOffset.UtcNow.AddDays(-10));

    private static DreamService CreateService(ILongTermMemory memory, DreamOptions options, ILlmClient llm) =>
        new(memory,
            [],
            llm,
            new AgentWorkSerializer(),
            new StubActivityMonitor(),
            new AgentClock(
                new ConfigurationBuilder().Build(),
                Options.Create(new AgentProfileOptions()),
                NullLogger<AgentClock>.Instance),
            Options.Create(options),
            Options.Create(new AgentProfileOptions()),
            NullLogger<DreamService>.Instance);

    /// <summary>Returns the scripted responses in order, then repeats the last one.</summary>
    private sealed class ScriptedLlmClient(params string[] responses) : ILlmClient
    {
        public List<string> Calls { get; } = [];

        public bool IsIdle => true;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(string.Join("\n", messages.Select(m => m.Text)));
            var index = Math.Min(Calls.Count - 1, responses.Length - 1);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responses[index])));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetResponseAsync(messages, options, cancellationToken);
    }

    private sealed class StubActivityMonitor : IUserActivityMonitor
    {
        public void RecordActivity() { }
        public bool IsUserActive(TimeSpan idleThreshold) => false;
    }

    private sealed class ArchivingStore : ILongTermMemory
    {
        private readonly Dictionary<string, MemoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Id, string Reason)> Archived { get; } = [];

        public IReadOnlyList<MemoryEntry> Snapshot() => [.. _entries.Values];

        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            _entries[entry.Id] = entry;
            return Task.CompletedTask;
        }

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.GetValueOrDefault(id));

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(
            MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>(
                [.. _entries.Values.Where(e => e.ArchivedAt is null)]);

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            _entries.Remove(id);
            return Task.CompletedTask;
        }

        public Task ArchiveAsync(string id, string reason, CancellationToken cancellationToken = default)
        {
            Archived.Add((id, reason));
            if (_entries.TryGetValue(id, out var entry))
                _entries[id] = entry with { ArchivedAt = DateTimeOffset.UtcNow, ArchiveReason = reason };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
