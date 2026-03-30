using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class ImportanceDecayTests
{
    [TestMethod]
    public async Task Decay_RecentEntry_IsNotDecayed()
    {
        var memory = new InMemoryStore();
        var entry = MakeEntry("id1", "Recent fact", daysOld: 5, importance: 0.8f);
        await memory.SaveAsync(entry);

        var service = CreateService(memory);
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.AreEqual(0.8f, result!.ImportanceScore, "Entry within grace period should not decay");
    }

    [TestMethod]
    public async Task Decay_OldEntry_IsDecayed()
    {
        var memory = new InMemoryStore();
        var entry = MakeEntry("id1", "Old fact", daysOld: 30, importance: 0.8f);
        await memory.SaveAsync(entry);

        var service = CreateService(memory);
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.AreEqual(0.75f, result!.ImportanceScore, 0.001, "Entry past grace period should lose 0.05");
    }

    [TestMethod]
    public async Task Decay_DoesNotGoBelowFloor()
    {
        var memory = new InMemoryStore();
        var entry = MakeEntry("id1", "Fading fact", daysOld: 60, importance: 0.12f);
        await memory.SaveAsync(entry);

        var service = CreateService(memory);
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.AreEqual(0.1f, result!.ImportanceScore, 0.001, "Importance should not drop below floor of 0.10");
    }

    [TestMethod]
    public async Task Decay_AtFloor_IsNotTouched()
    {
        var memory = new InMemoryStore();
        var entry = MakeEntry("id1", "Floor fact", daysOld: 90, importance: 0.1f);
        await memory.SaveAsync(entry);

        var service = CreateService(memory);
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.AreEqual(0.1f, result!.ImportanceScore, "Entry at floor should not be modified");
    }

    [TestMethod]
    public async Task Decay_UpdatedRecently_IsNotDecayed()
    {
        var memory = new InMemoryStore();
        // Created long ago but updated recently
        var entry = new MemoryEntry(
            "id1", "Updated fact", null, [], DateTimeOffset.UtcNow.AddDays(-60),
            UpdatedAt: DateTimeOffset.UtcNow.AddDays(-3),
            ImportanceScore: 0.7f);
        await memory.SaveAsync(entry);

        var service = CreateService(memory);
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.AreEqual(0.7f, result!.ImportanceScore, "Recently updated entry should not decay");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MemoryEntry MakeEntry(string id, string content, int daysOld, float importance) =>
        new(id, content, null, [], DateTimeOffset.UtcNow.AddDays(-daysOld), ImportanceScore: importance);

    private static DreamService CreateService(ILongTermMemory memory) =>
        new(memory,
            [],
            new StubLlmClient(),
            new StubActivityMonitor(),
            new AgentClock(
                new ConfigurationBuilder().Build(),
                Options.Create(new AgentProfileOptions()),
                NullLogger<AgentClock>.Instance),
            Options.Create(new DreamOptions { Enabled = false }),
            Options.Create(new AgentProfileOptions()),
            NullLogger<DreamService>.Instance);

    private sealed class StubLlmClient : ILlmClient
    {
        public bool IsIdle => true;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

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

    private sealed class InMemoryStore : ILongTermMemory
    {
        private readonly Dictionary<string, MemoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            _entries[entry.Id] = entry;
            return Task.CompletedTask;
        }

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.GetValueOrDefault(id));

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(
            MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([.. _entries.Values]);

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            _entries.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
