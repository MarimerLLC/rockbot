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
    public async Task Decay_SaveBumpsUpdatedAt_SoSuccessivePassesDoNotDoubleCount()
    {
        // Regression: the first implementation preserved UpdatedAt on decay save, which
        // meant each pass re-measured elapsed time from the original UpdatedAt. Over N
        // passes, decay applied roughly N times the intended amount because each pass's
        // "since last touch" was growing unbounded.
        //
        // Fix: decay save bumps UpdatedAt = now. This test runs two back-to-back passes
        // on the SAME in-memory store (no synthetic UpdatedAt manipulation) and verifies
        // that the second pass applies near-zero decay because almost no calendar time
        // has elapsed since the first pass's save.
        var memory = new InMemoryStore();
        var lastSeen = DateTimeOffset.UtcNow.AddDays(-60);
        var entry = new MemoryEntry("id1", "x", null, [], lastSeen, UpdatedAt: lastSeen, ImportanceScore: 0.8f)
        {
            LastSeenAt = lastSeen
        };
        await memory.SaveAsync(entry);

        var opts = new DreamOptions
        {
            Enabled = false,
            ImportanceDecayGraceDays = 0,
            ImportanceDecayHalfLifeDays = 45f,
            ImportanceDecayFloor = 0.0f
        };
        var service = CreateService(memory, opts);

        await service.RunImportanceDecayPassAsync([entry]);
        var afterFirst = (await memory.GetAsync("id1"))!;
        var importanceAfterFirst = afterFirst.ImportanceScore;

        await service.RunImportanceDecayPassAsync([afterFirst]);
        var afterSecond = (await memory.GetAsync("id1"))!;

        Assert.AreEqual(importanceAfterFirst, afterSecond.ImportanceScore, 0.002,
            "A second pass run immediately after the first must apply near-zero decay — " +
            "UpdatedAt should have been bumped to 'now' on the first save.");
    }

    [TestMethod]
    public async Task Decay_PastGrace_AppliesElapsedTimeMultiplicativeDecay()
    {
        var memory = new InMemoryStore();
        // Entry LastSeen 40 days ago, UpdatedAt 1 day ago (simulating a prior decay pass).
        // Under grace=14, halflife=30: eligibleElapsed = min(1, 40-14) = 1 day.
        // Expected: 0.8 * 0.5^(1/30) = 0.8 * 0.97716 = 0.78173.
        var lastSeen = DateTimeOffset.UtcNow.AddDays(-40);
        var lastUpdated = DateTimeOffset.UtcNow.AddDays(-1);
        var entry = new MemoryEntry(
            "id1", "Old fact", null, [], lastSeen,
            UpdatedAt: lastUpdated,
            ImportanceScore: 0.8f)
        {
            LastSeenAt = lastSeen
        };
        await memory.SaveAsync(entry);

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
        var expected = 0.8f * (float)Math.Pow(0.5, 1.0 / 30.0);
        Assert.AreEqual(expected, result!.ImportanceScore, 0.001,
            "Decay should multiply by 0.5^(elapsedDays / halfLife) based on actual calendar elapsed time.");
    }

    [TestMethod]
    public async Task Decay_JustPastGrace_AppliesOnlyEligibleElapsed()
    {
        // First decay pass after grace expires should only apply (daysSinceSeen - grace) worth
        // of decay, not daysSinceLastTouch, so we don't retroactively decay into the grace window.
        var memory = new InMemoryStore();
        // LastSeen 30.5 days ago, UpdatedAt is also old (e.g. entry was never touched since creation).
        // grace = 30, halflife = 10. eligibleElapsed = min(30.5-30, 30.5) = 0.5 days.
        // NOT min(halflife's worth of catch-up, ...).
        var lastSeen = DateTimeOffset.UtcNow.AddDays(-30.5);
        var entry = new MemoryEntry("id1", "Fact", null, [], lastSeen, ImportanceScore: 0.8f)
        {
            LastSeenAt = lastSeen
        };
        await memory.SaveAsync(entry);

        var opts = new DreamOptions
        {
            Enabled = false,
            ImportanceDecayGraceDays = 30,
            ImportanceDecayHalfLifeDays = 10f,
            ImportanceDecayFloor = 0.10f
        };
        var service = CreateService(memory, opts);
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        var expected = 0.8f * (float)Math.Pow(0.5, 0.5 / 10.0);
        Assert.AreEqual(expected, result!.ImportanceScore, 0.001,
            "First-past-grace decay must apply only the post-grace elapsed, not the full time since UpdatedAt.");
    }

    [TestMethod]
    public async Task Decay_CalendarTimeInvariant_SmallOrLargePassesMatch()
    {
        // The core property: decay is calendar-time invariant. Applying one large pass covering
        // N days should produce the same importance as N smaller passes (simulating different
        // cron cadences).
        var opts = new DreamOptions
        {
            Enabled = false,
            ImportanceDecayGraceDays = 0,
            ImportanceDecayHalfLifeDays = 45f,
            ImportanceDecayFloor = 0.0f
        };

        // Path A: one big pass covering 10 days
        var memoryA = new InMemoryStore();
        var tenDaysAgo = DateTimeOffset.UtcNow.AddDays(-10);
        var entryA = new MemoryEntry("a", "x", null, [], tenDaysAgo, UpdatedAt: tenDaysAgo, ImportanceScore: 0.9f)
        {
            LastSeenAt = tenDaysAgo
        };
        await memoryA.SaveAsync(entryA);
        var serviceA = CreateService(memoryA, opts);
        await serviceA.RunImportanceDecayPassAsync([entryA]);
        var resultA = await memoryA.GetAsync("a");

        // Path B: 20 small passes simulating 0.5-day spacing (12h cron) over 10 calendar days.
        // In a real running system, each decay pass bumps UpdatedAt to "now"; the NEXT pass
        // 0.5 days later sees UpdatedAt as 0.5d old. We simulate that by constructing each
        // iteration's entry with UpdatedAt pinned 0.5 days before the test's real-time "now."
        var memoryB = new InMemoryStore();
        var lastSeenB = DateTimeOffset.UtcNow.AddDays(-10);
        var currentImportance = 0.9f;
        for (var i = 0; i < 20; i++)
        {
            var entryB = new MemoryEntry(
                "b", "x", null, [], lastSeenB,
                UpdatedAt: DateTimeOffset.UtcNow.AddDays(-0.5),
                ImportanceScore: currentImportance)
            {
                LastSeenAt = lastSeenB
            };
            await memoryB.SaveAsync(entryB);

            var serviceB = CreateService(memoryB, opts);
            await serviceB.RunImportanceDecayPassAsync([entryB]);
            currentImportance = (await memoryB.GetAsync("b"))!.ImportanceScore;
        }

        // Path A applied 10 days of decay in one pass; path B applied 20 × 0.5 days.
        // Both should arrive at the same importance.
        Assert.AreEqual(resultA!.ImportanceScore, currentImportance, 0.01,
            $"Calendar-time decay should be cadence-invariant: one 10-day pass ({resultA.ImportanceScore:F4}) " +
            $"should match twenty 0.5-day passes ({currentImportance:F4}).");
    }

    [TestMethod]
    public async Task Decay_DoesNotGoBelowFloor()
    {
        var memory = new InMemoryStore();
        // Aggressive config so one pass drives well below floor without the clamp.
        var lastSeen = DateTimeOffset.UtcNow.AddDays(-10);
        var entry = new MemoryEntry(
            "id1", "Fading fact", null, [], lastSeen,
            UpdatedAt: lastSeen,
            ImportanceScore: 0.15f)
        {
            LastSeenAt = lastSeen
        };
        await memory.SaveAsync(entry);

        var opts = new DreamOptions
        {
            Enabled = false,
            ImportanceDecayGraceDays = 0,
            ImportanceDecayHalfLifeDays = 0.5f,   // 0.15 × 0.5^(10/0.5) ≪ floor
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
        // Created long ago but reinforced (LastSeenAt) recently — within default 30-day grace.
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
        Assert.AreEqual(0.7f, result!.ImportanceScore, "Recently-reinforced entry within grace should not decay.");
    }

    [TestMethod]
    public async Task Decay_DreamRewrite_DoesNotProtectCumulativeDecay()
    {
        // Specific shift from pre-0.10: dream housekeeping bumps UpdatedAt but does NOT
        // permanently shield the entry from decay. Under elapsed-time decay, a recent
        // UpdatedAt does delay decay within a single cycle, but over the agent's lifetime
        // the decay still accumulates because LastSeenAt never advances for entries that
        // are never actually reinforced.
        //
        // Here we simulate a scenario where UpdatedAt is repeatedly bumped (as if dream
        // rewrites the entry every cycle) and verify the entry still decays over calendar
        // time, just more slowly per pass.
        var memory = new InMemoryStore();
        var opts = new DreamOptions
        {
            Enabled = false,
            ImportanceDecayGraceDays = 0,
            ImportanceDecayHalfLifeDays = 10f,
            ImportanceDecayFloor = 0.0f
        };

        var lastSeen = DateTimeOffset.UtcNow.AddDays(-30); // 30 days past with no reinforcement
        var currentImportance = 0.9f;

        // Simulate 60 passes over 30 days (2/day), with UpdatedAt bumped to "now - 0.5d" each time
        // (i.e. dream touched the entry each cycle).
        for (var i = 0; i < 60; i++)
        {
            var updatedAt = DateTimeOffset.UtcNow.AddDays(-0.5);
            var entry = new MemoryEntry(
                "id1", "Stale but polished", null, [], lastSeen,
                UpdatedAt: updatedAt,
                ImportanceScore: currentImportance)
            {
                LastSeenAt = lastSeen
            };
            await memory.SaveAsync(entry);

            var service = CreateService(memory, opts);
            await service.RunImportanceDecayPassAsync([entry]);
            currentImportance = (await memory.GetAsync("id1"))!.ImportanceScore;
        }

        // 30 days of calendar decay at halflife=10 → 3 half-lives → 0.125x
        var expected = 0.9f * (float)Math.Pow(0.5, 30.0 / 10.0);
        Assert.AreEqual(expected, currentImportance, 0.02,
            "Dream rewrites must not protect an entry from cumulative calendar-time decay.");
    }

    [TestMethod]
    public async Task Decay_CustomGraceAndHalfLife_OverrideDefaults()
    {
        var memory = new InMemoryStore();
        // grace=5, halflife=0.5. Entry LastSeen 5.5d ago, UpdatedAt 0.5d ago.
        // eligibleElapsed = min(5.5-5, 0.5) = 0.5d. factor = 0.5^(0.5/0.5) = 0.5 exactly.
        var lastSeen = DateTimeOffset.UtcNow.AddDays(-5.5);
        var entry = new MemoryEntry(
            "id1", "Test fact", null, [], lastSeen,
            UpdatedAt: DateTimeOffset.UtcNow.AddDays(-0.5),
            ImportanceScore: 0.8f)
        {
            LastSeenAt = lastSeen
        };
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
            "Custom half-life of 0.5d with 0.5d elapsed should halve importance exactly.");
    }

    [TestMethod]
    public async Task Decay_HalfLifeBehavior_SingleElapsedPassMatchesFormula()
    {
        // Verify the closed-form: after t days of decay, importance ≈ start × 0.5^(t/halflife).
        // Single pass covering a full halflife should halve.
        var memory = new InMemoryStore();
        var lastSeen = DateTimeOffset.UtcNow.AddDays(-10);
        var entry = new MemoryEntry("id1", "Fact", null, [], lastSeen, UpdatedAt: lastSeen, ImportanceScore: 0.8f)
        {
            LastSeenAt = lastSeen
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
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.AreEqual(0.4f, result!.ImportanceScore, 0.002,
            "10 days of decay at 10-day halflife should halve the importance.");
    }

    [TestMethod]
    public async Task Decay_CoreFactOverSixMonths_ReachesFloorWithDefaults()
    {
        // End-to-end shape check: a 0.95 core fact with default options (grace=30, halflife=45,
        // floor=0.10) should reach the floor near the 6-month mark. Run as a single elapsed-time
        // pass representing the full calendar window.
        var memory = new InMemoryStore();
        var lastSeen = DateTimeOffset.UtcNow.AddDays(-180);
        var entry = new MemoryEntry("id1", "Core fact", null, [], lastSeen, UpdatedAt: lastSeen, ImportanceScore: 0.95f)
        {
            LastSeenAt = lastSeen
        };
        await memory.SaveAsync(entry);

        var service = CreateService(memory, new DreamOptions { Enabled = false });
        await service.RunImportanceDecayPassAsync([entry]);

        var result = await memory.GetAsync("id1");
        Assert.IsTrue(result!.ImportanceScore <= 0.11f,
            $"With defaults (grace=30, halflife=45), a 0.95 core fact should reach floor in ~6 months (got {result.ImportanceScore:F3}).");
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
