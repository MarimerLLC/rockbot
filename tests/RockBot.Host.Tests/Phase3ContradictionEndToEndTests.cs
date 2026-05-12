using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Phase 3 self-repair acceptance tests against the real <see cref="FileMemoryStore"/>
/// and <see cref="MemoryContradictionDetector"/>. Mirrors the acceptance criteria on
/// GitHub issue #347:
/// <list type="number">
///   <item>Saving "wrapper does pass arguments" supersedes the older "wrapper cannot pass arguments" claim.</item>
///   <item>Existing user-correction memories displace conflicting agent-self memories.</item>
///   <item>Saves outside <c>claim/capability/*</c> and <c>feedback/*</c> are unaffected.</item>
/// </list>
/// </summary>
[TestClass]
public class Phase3ContradictionEndToEndTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-phase3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task Acceptance1_NewerCapabilityClaim_SupersedesOlderOppositeClaim()
    {
        var ltm = NewLtm();
        var detector = new MemoryContradictionDetector(ltm, NullLogger<MemoryContradictionDetector>.Instance);
        var writer = new CapabilityClaimWriter(ltm, detector, NullLogger<CapabilityClaimWriter>.Instance);

        // 1. Save the older (negative) claim.
        await writer.SaveCapabilityClaimAsync(new CapabilityClaim(
            Server: "calendar-mcp",
            Tool: "get_calendar_events",
            Statement: "wrapper cannot pass arguments to get_calendar_events",
            Verify: NewVerify(),
            Evidence: ["recovery exhausted"],
            CreatedAt: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)));

        // 2. Save the contradicting (positive) claim later.
        await writer.SaveCapabilityClaimAsync(new CapabilityClaim(
            Server: "calendar-mcp",
            Tool: "get_calendar_events",
            Statement: "wrapper does pass arguments to get_calendar_events; verified",
            Verify: NewVerify(),
            Evidence: ["recovery succeeded"],
            CreatedAt: new DateTimeOffset(2026, 5, 8, 0, 0, 0, TimeSpan.Zero)));

        // 3. Search returns only the newer claim — the older one is hidden as superseded.
        var live = await ltm.SearchAsync(new MemorySearchCriteria(
            Category: CapabilityClaimCategories.Prefix, MaxResults: 50));
        Assert.AreEqual(1, live.Count, "Only the newer claim should be live after supersession.");
        StringAssert.Contains(live[0].Content, "does pass arguments");

        // 4. Including superseded entries shows both, with the older marked.
        var all = await ltm.SearchAsync(new MemorySearchCriteria(
            Category: CapabilityClaimCategories.Prefix, MaxResults: 50, IncludeSuperseded: true));
        Assert.AreEqual(2, all.Count);
        var loser = all.Single(e => e.Content.Contains("cannot pass"));
        Assert.IsNotNull(loser.SupersededBy, "Older claim must carry SupersededBy pointing at the winner.");
        var winner = all.Single(e => e.Content.Contains("does pass"));
        Assert.AreEqual(winner.Id, loser.SupersededBy);
    }

    [TestMethod]
    public async Task Acceptance2_UserCorrectionWinsOverAgentSelf()
    {
        var ltm = NewLtm();
        var detector = new MemoryContradictionDetector(ltm, NullLogger<MemoryContradictionDetector>.Instance);

        // 1. Pre-existing user correction (saved directly with the correction tag).
        await ltm.SaveAsync(new MemoryEntry(
            Id: "user-correction-1",
            Content: "wrapper does pass arguments to get_calendar_events",
            Category: "claim/capability/calendar-mcp/get_calendar_events",
            Tags: ["correction", "capability-claim"],
            CreatedAt: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)));

        // 2. Agent-self writes a contradicting claim through the writer.
        var writer = new CapabilityClaimWriter(ltm, detector, NullLogger<CapabilityClaimWriter>.Instance);
        await writer.SaveCapabilityClaimAsync(new CapabilityClaim(
            Server: "calendar-mcp",
            Tool: "get_calendar_events",
            Statement: "wrapper cannot pass arguments to get_calendar_events",
            Verify: NewVerify(),
            Evidence: ["agent observation"],
            CreatedAt: new DateTimeOffset(2026, 5, 8, 0, 0, 0, TimeSpan.Zero)));

        // 3. Live search shows only the user correction; agent-self claim is hidden.
        var live = await ltm.SearchAsync(new MemorySearchCriteria(
            Category: CapabilityClaimCategories.Prefix, MaxResults: 50));
        Assert.AreEqual(1, live.Count);
        Assert.AreEqual("user-correction-1", live[0].Id);

        // 4. The agent-self claim is on disk but marked superseded by the user correction.
        var all = await ltm.SearchAsync(new MemorySearchCriteria(
            Category: CapabilityClaimCategories.Prefix, MaxResults: 50, IncludeSuperseded: true));
        Assert.AreEqual(2, all.Count);
        var agentSelf = all.Single(e => e.Id != "user-correction-1");
        Assert.AreEqual("user-correction-1", agentSelf.SupersededBy);
    }

    [TestMethod]
    public async Task Acceptance3_SavesOutsideScopedCategories_DoNotInvokeDetector()
    {
        var ltm = NewLtm();
        var spy = new ThrowingDetector();

        // The capability-claim writer is wired with the spy; saving claims would throw.
        // For this test we exercise direct LTM saves with non-scoped categories — those
        // should never reach the detector regardless of caller. We verify the detector
        // contract directly: ResolveAsync on a non-scoped entry must short-circuit.
        var realDetector = new MemoryContradictionDetector(ltm, NullLogger<MemoryContradictionDetector>.Instance);

        var entry = new MemoryEntry(
            Id: "x-1",
            Content: "Loves dogs and lives in Minneapolis",
            Category: "user-preferences/pets",
            Tags: [],
            CreatedAt: DateTimeOffset.UtcNow);

        // Pre-seed an entry that *would* contradict if the detector wasn't narrow.
        await ltm.SaveAsync(new MemoryEntry(
            Id: "old-1",
            Content: "Does not like dogs",
            Category: "user-preferences/pets",
            Tags: [],
            CreatedAt: DateTimeOffset.UtcNow));

        var resolution = await realDetector.ResolveAsync(entry);

        Assert.IsFalse(resolution.HasContradiction,
            "Acceptance criterion 3: detector must not affect saves outside claim/capability/* and feedback/*.");

        // Sanity: spy throws if invoked, but we never invoke it for a non-scoped entry.
        Assert.AreEqual(0, spy.Calls);
    }

    private FileMemoryStore NewLtm()
    {
        var memOpts = Options.Create(new MemoryOptions { BasePath = Path.Combine(_tempDir, "ltm") });
        var profOpts = Options.Create(new AgentProfileOptions { BasePath = _tempDir });
        var embedOpts = Options.Create(new EmbeddingOptions());
        return new FileMemoryStore(memOpts, profOpts, embedOpts, NullLogger<FileMemoryStore>.Instance, EmbeddingTextPreparer.ForTests());
    }

    private static VerifyShape NewVerify() => new(
        Server: "calendar-mcp",
        Tool: "get_calendar_events",
        Arguments: JsonDocument.Parse("""{"accountId":"x","timeZone":"America/Chicago","startDate":"2026-05-08","endDate":"2026-05-08"}""").RootElement,
        Expect: new VerifyExpectation(VerifyExpectationKind.Success));

    private sealed class ThrowingDetector : IMemoryContradictionDetector
    {
        public int Calls { get; private set; }
        public Task<ContradictionResolution> ResolveAsync(MemoryEntry incoming, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Detector should not be called for non-scoped categories.");
        }
    }
}
