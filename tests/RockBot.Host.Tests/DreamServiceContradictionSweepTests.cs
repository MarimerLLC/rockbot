using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Phase 3 self-repair: dream-pass backstop tests. Exercise
/// <see cref="DreamService.ApplyContradictionSweepResultAsync"/> directly with a
/// real <see cref="FileMemoryStore"/> so the supersession marker round-trips
/// through disk, then verify acceptance criterion 2 — existing user-correction
/// memories displace conflicting agent-self memories on the next sweep.
/// </summary>
[TestClass]
public class DreamServiceContradictionSweepTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task Acceptance2_DreamSweep_UserCorrectionDisplacesAgentSelf()
    {
        var ltm = NewLtm();

        var userCorrection = new MemoryEntry(
            Id: "user-correction-1",
            Content: "wrapper does pass arguments to get_calendar_events",
            Category: "claim/capability/calendar-mcp/get_calendar_events",
            Tags: ["correction", "capability-claim"],
            CreatedAt: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));

        var agentSelf = new MemoryEntry(
            Id: "agent-self-1",
            Content: "wrapper cannot pass arguments to get_calendar_events",
            Category: "claim/capability/calendar-mcp/get_calendar_events",
            Tags: ["capability-claim"],
            CreatedAt: new DateTimeOffset(2026, 5, 8, 0, 0, 0, TimeSpan.Zero));

        await ltm.SaveAsync(userCorrection);
        await ltm.SaveAsync(agentSelf);

        // LLM in the sweep would emit this pair: user-correction wins.
        var corpus = new[] { userCorrection, agentSelf };
        var pairs = new[]
        {
            new DreamService.ContradictionPairDto(
                WinnerId: "user-correction-1",
                LoserId: "agent-self-1",
                Reason: "user correction"),
        };

        var count = await DreamService.ApplyContradictionSweepResultAsync(
            ltm, corpus, pairs, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(1, count);

        var live = await ltm.SearchAsync(new MemorySearchCriteria(
            Category: CapabilityClaimCategories.Prefix, MaxResults: 50));
        Assert.AreEqual(1, live.Count, "Only the user correction should remain live after the sweep.");
        Assert.AreEqual("user-correction-1", live[0].Id);

        var loser = await ltm.GetAsync("agent-self-1");
        Assert.IsNotNull(loser);
        Assert.AreEqual("user-correction-1", loser!.SupersededBy);
    }

    [TestMethod]
    public async Task Sweep_ProtectsUserCorrection_FromBeingSupersededByNonCorrection()
    {
        var ltm = NewLtm();

        var userCorrection = new MemoryEntry(
            Id: "user-correction-1",
            Content: "Always use bullet points",
            Category: "feedback/from-user/style",
            Tags: ["correction"],
            CreatedAt: DateTimeOffset.UtcNow);

        var agentSelf = new MemoryEntry(
            Id: "agent-self-1",
            Content: "Never use bullet points",
            Category: "feedback/from-agent/style",
            Tags: [],
            CreatedAt: DateTimeOffset.UtcNow);

        await ltm.SaveAsync(userCorrection);
        await ltm.SaveAsync(agentSelf);

        // LLM erroneously proposes superseding the user correction with the agent-self entry.
        var corpus = new[] { userCorrection, agentSelf };
        var pairs = new[]
        {
            new DreamService.ContradictionPairDto(
                WinnerId: "agent-self-1",
                LoserId: "user-correction-1",
                Reason: "more recent"),
        };

        var count = await DreamService.ApplyContradictionSweepResultAsync(
            ltm, corpus, pairs, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(0, count, "Sweep must refuse to supersede a user correction with a non-correction.");

        var fetched = await ltm.GetAsync("user-correction-1");
        Assert.IsNotNull(fetched);
        Assert.IsNull(fetched!.SupersededBy);
    }

    [TestMethod]
    public async Task Sweep_IgnoresPairs_WithUnknownIdsOrSelfReferences()
    {
        var ltm = NewLtm();
        var entry = new MemoryEntry(
            Id: "real-1",
            Content: "Always X",
            Category: "feedback/style",
            Tags: [],
            CreatedAt: DateTimeOffset.UtcNow);
        await ltm.SaveAsync(entry);

        var pairs = new[]
        {
            new DreamService.ContradictionPairDto("ghost-1", "real-1", "no winner"),
            new DreamService.ContradictionPairDto("real-1", "ghost-2", "no loser"),
            new DreamService.ContradictionPairDto("real-1", "real-1", "self"),
            new DreamService.ContradictionPairDto(null, "real-1", "missing"),
            new DreamService.ContradictionPairDto("real-1", "", "empty"),
        };

        var count = await DreamService.ApplyContradictionSweepResultAsync(
            ltm, [entry], pairs, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(0, count);

        var fetched = await ltm.GetAsync("real-1");
        Assert.IsNull(fetched!.SupersededBy);
    }

    private FileMemoryStore NewLtm()
    {
        var memOpts = Options.Create(new MemoryOptions { BasePath = Path.Combine(_tempDir, "ltm") });
        var profOpts = Options.Create(new AgentProfileOptions { BasePath = _tempDir });
        var embedOpts = Options.Create(new EmbeddingOptions());
        return new FileMemoryStore(memOpts, profOpts, embedOpts, NullLogger<FileMemoryStore>.Instance, EmbeddingTextPreparer.ForTests());
    }
}
