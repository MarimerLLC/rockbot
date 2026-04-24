using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class ImportanceDecayTests
{
    [TestMethod]
    public async Task Decay_WithinGracePeriod_IsNotDecayed()
    {
        var memory = new InMemoryStore();
        // 5 days old, default grace = 30 days → protected
        var entry = MakeEntry("id1", "Recent fact", daysOld: 5, importance: 0.8f);
        await memory.SaveAsync(entry);

        var service = CreateService(memory, new DreamOptions { Enabled = false });
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.AreEqual(0.8f, result!.ImportanceScore, "Entry within grace period should not decay.");
    }

    [TestMethod]
    public async Task Decay_PastGracePeriod_MultipliesByPerCycleFactor()
    {
        var memory = new InMemoryStore();
        var entry = MakeEntry("id1", "Old fact", daysOld: 60, importance: 0.8f);
        await memory.SaveAsync(entry);

        // Grace=14, HalfLife=30. Per-cycle factor = 0.5^(1/60) ≈ 0.98853.
        var opts = new DreamOptions
        {
            Enabled = false,
            ImportanceDecayGraceDays = 14,
            ImportanceDecayHalfLifeDays = 30f,
            ImportanceDecayFloor = 0.10f
        };
        var service = CreateService(memory, opts);
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        var expectedFactor = (float)Math.Pow(0.5, 1.0 / (30.0 * 2.0));
        Assert.AreEqual(0.8f * expectedFactor, result!.ImportanceScore, 0.0005,
            "One decay pass should apply exactly one per-cycle factor.");
    }

    [TestMethod]
    public async Task Decay_DoesNotGoBelowFloor()
    {
        var memory = new InMemoryStore();
        // Near-floor entry with an aggressive config: one cycle would drive it below floor
        // if unchecked (0.15 * 0.5 = 0.075).
        var entry = MakeEntry("id1", "Fading fact", daysOld: 60, importance: 0.15f);
        await memory.SaveAsync(entry);

        var opts = new DreamOptions
        {
            Enabled = false,
            ImportanceDecayGraceDays = 0,
            ImportanceDecayHalfLifeDays = 0.5f,   // 12h halflife at 2 cycles/day → factor = 0.5 per cycle
            ImportanceDecayFloor = 0.10f
        };
        var service = CreateService(memory, opts);
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.AreEqual(0.10f, result!.ImportanceScore, 0.001, "Importance must not drop below the configured floor.");
    }

    [TestMethod]
    public async Task Decay_AtFloor_IsNotTouched()
    {
        var memory = new InMemoryStore();
        var entry = MakeEntry("id1", "Floor fact", daysOld: 90, importance: 0.10f);
        await memory.SaveAsync(entry);

        var service = CreateService(memory, new DreamOptions { Enabled = false });
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.AreEqual(0.10f, result!.ImportanceScore, "Entry at floor should not be modified.");
    }

    [TestMethod]
    public async Task Decay_ReinforcedRecently_IsNotDecayed()
    {
        var memory = new InMemoryStore();
        // Created long ago but reinforced (LastSeenAt) recently — a merge pulled in a fresh source.
        var entry = new MemoryEntry(
            "id1", "Reinforced fact", null, [], DateTimeOffset.UtcNow.AddDays(-60),
            UpdatedAt: DateTimeOffset.UtcNow.AddDays(-3),
            ImportanceScore: 0.7f)
        {
            LastSeenAt = DateTimeOffset.UtcNow.AddDays(-3),
            ReinforcementCount = 2
        };
        await memory.SaveAsync(entry);

        var service = CreateService(memory, new DreamOptions { Enabled = false });
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.AreEqual(0.7f, result!.ImportanceScore, "Recently-reinforced entry should not decay.");
    }

    [TestMethod]
    public async Task Decay_DreamRewriteWithoutReinforcement_IsDecayed()
    {
        var memory = new InMemoryStore();
        // The load-bearing behavior shift: an entry rewritten recently by dream housekeeping
        // (bumping UpdatedAt) but never actually reinforced (LastSeenAt still the original
        // CreatedAt) must now decay. Pre-0.10 it was protected by the recent UpdatedAt.
        var oldTime = DateTimeOffset.UtcNow.AddDays(-60);
        var entry = new MemoryEntry(
            "id1", "Stale fact that dream keeps polishing", null, [], oldTime,
            UpdatedAt: DateTimeOffset.UtcNow.AddHours(-1),
            ImportanceScore: 0.7f)
        {
            LastSeenAt = oldTime,
            ReinforcementCount = 1
        };
        await memory.SaveAsync(entry);

        var service = CreateService(memory, new DreamOptions { Enabled = false });
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.IsTrue(result!.ImportanceScore < 0.7f,
            "Dream rewrite alone must not reset the decay clock — only real reinforcement does.");
    }

    [TestMethod]
    public async Task Decay_CustomGraceAndHalfLife_OverrideDefaults()
    {
        var memory = new InMemoryStore();
        // Entry would be within the default 30-day grace (20 days old), but we set grace=5
        // and halflife=0.5 (factor = 0.5 per cycle), so it SHOULD decay by exactly half.
        var entry = MakeEntry("id1", "Test fact", daysOld: 20, importance: 0.8f);
        await memory.SaveAsync(entry);

        var opts = new DreamOptions
        {
            Enabled = false,
            ImportanceDecayGraceDays = 5,
            ImportanceDecayHalfLifeDays = 0.5f,
            ImportanceDecayFloor = 0.10f
        };
        var service = CreateService(memory, opts);
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.AreEqual(0.4f, result!.ImportanceScore, 0.001,
            "With halflife=0.5d at 2 cycles/day, one cycle halves the importance.");
    }

    [TestMethod]
    public async Task Decay_HalfLifeBehavior_ApproximatesFiftyPercentOverHalfLife()
    {
        // Run multiple decay passes and verify that after a number of cycles equal to
        // (halflife * 2 cycles/day), the importance is roughly halved. This locks the
        // exponential shape in place across iterations.
        var memory = new InMemoryStore();
        var oldTime = DateTimeOffset.UtcNow.AddDays(-100);
        var entry = new MemoryEntry("id1", "Fact", null, [], oldTime, ImportanceScore: 0.8f)
        {
            LastSeenAt = oldTime
        };
        await memory.SaveAsync(entry);

        var opts = new DreamOptions
        {
            Enabled = false,
            ImportanceDecayGraceDays = 0,
            ImportanceDecayHalfLifeDays = 10f,
            ImportanceDecayFloor = 0.0f
        };
        var service = CreateService(memory, opts);

        // 10-day halflife at 2 cycles/day = 20 cycles for one halving
        for (var i = 0; i < 20; i++)
        {
            var current = await memory.GetAsync("id1");
            await service.RunImportanceDecayPassAsync([current!]);
        }

        var final = await memory.GetAsync("id1");
        Assert.AreEqual(0.4f, final!.ImportanceScore, 0.005,
            "After one halflife worth of cycles, importance should be ~50% of starting value.");
    }

    [TestMethod]
    public async Task Decay_CoreFactOverSixMonths_ReachesFloorWithDefaults()
    {
        // Shape check: a 0.95 "core fact" with default options (grace=30, halflife=45, floor=0.10,
        // 2 cycles/day) should reach the floor near the 6-month mark. We simulate 360 dream
        // cycles (180 days at 2 cycles/day) of continuous non-reinforcement and verify the entry
        // lands at the floor.
        var memory = new InMemoryStore();
        // Place LastSeenAt 180 days in the past so grace is exhausted and effective decay is 150 days.
        var startedAt = DateTimeOffset.UtcNow.AddDays(-180);
        var entry = new MemoryEntry("id1", "Core fact", null, [], startedAt, ImportanceScore: 0.95f)
        {
            LastSeenAt = startedAt
        };
        await memory.SaveAsync(entry);

        var service = CreateService(memory, new DreamOptions { Enabled = false });

        // Simulate 300 cycles of housekeeping (~5 months of dream passes at 2/day, on top of
        // the implied time already elapsed). This is an approximation — the test is about
        // the order of magnitude, not the exact day.
        for (var i = 0; i < 300; i++)
        {
            var current = await memory.GetAsync("id1");
            await service.RunImportanceDecayPassAsync([current!]);
        }

        var final = await memory.GetAsync("id1");
        Assert.IsTrue(final!.ImportanceScore <= 0.15f,
            $"Default decay shape should drive a 0.95 core fact close to the 0.10 floor within a 6-month window (got {final.ImportanceScore:F3}).");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MemoryEntry MakeEntry(string id, string content, int daysOld, float importance) =>
        new(id, content, null, [], DateTimeOffset.UtcNow.AddDays(-daysOld), ImportanceScore: importance);

    private static DreamService CreateService(ILongTermMemory memory, DreamOptions options) =>
        new(memory,
            [],
            new StubLlmClient(),
            new AgentWorkSerializer(),
            new StubActivityMonitor(),
            new AgentClock(
                new ConfigurationBuilder().Build(),
                Options.Create(new AgentProfileOptions()),
                NullLogger<AgentClock>.Instance),
            Options.Create(options),
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
