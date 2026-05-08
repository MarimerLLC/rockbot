using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

/// <summary>
/// Unit tests for the Phase 3 hot-path contradiction detector.
/// Covers narrow scope (capability claims and feedback only), valence inversion,
/// user-correction always-wins protection, and ambiguity-skip behaviour.
/// </summary>
[TestClass]
public class MemoryContradictionDetectorTests
{
    [TestMethod]
    public async Task ResolveAsync_OutsideScopedCategories_ReturnsNoneWithoutScanningStore()
    {
        var memory = new RecordingMemory(); // SearchAsync throws if called
        var detector = NewDetector(memory);

        var entry = NewEntry("user-preferences/pets", "Loves dogs", id: "x-1");

        var resolution = await detector.ResolveAsync(entry);

        Assert.IsFalse(resolution.HasContradiction);
        Assert.AreEqual(0, memory.SearchCallCount,
            "Narrow scope: detector must not even query the store for non-claim, non-feedback writes.");
    }

    [TestMethod]
    public async Task ResolveAsync_CapabilityClaim_OppositeValence_NewerWins()
    {
        var memory = new RecordingMemory();
        var oldClaim = NewEntry(
            category: "claim/capability/calendar-mcp/get_calendar_events",
            content: "wrapper cannot pass arguments to get_calendar_events",
            id: "old-1");
        memory.Existing.Add(oldClaim);

        var detector = NewDetector(memory);
        var incoming = NewEntry(
            category: "claim/capability/calendar-mcp/get_calendar_events",
            content: "wrapper does pass arguments to get_calendar_events; verified by call",
            id: "new-1");

        var resolution = await detector.ResolveAsync(incoming);

        Assert.IsTrue(resolution.HasContradiction);
        Assert.IsNull(resolution.IncomingSupersededBy);
        CollectionAssert.AreEqual(new[] { "old-1" }, resolution.ExistingIdsToSupersede.ToArray());
    }

    [TestMethod]
    public async Task ResolveAsync_CapabilityClaim_SameValence_NoContradiction()
    {
        var memory = new RecordingMemory();
        memory.Existing.Add(NewEntry(
            category: "claim/capability/calendar-mcp/get_calendar_events",
            content: "wrapper cannot pass arguments to get_calendar_events",
            id: "old-1"));

        var detector = NewDetector(memory);
        var incoming = NewEntry(
            category: "claim/capability/calendar-mcp/get_calendar_events",
            content: "wrapper cannot enumerate accounts either",
            id: "new-1");

        var resolution = await detector.ResolveAsync(incoming);

        Assert.IsFalse(resolution.HasContradiction);
    }

    [TestMethod]
    public async Task ResolveAsync_CapabilityClaim_DifferentTool_NoContradiction()
    {
        var memory = new RecordingMemory();
        memory.Existing.Add(NewEntry(
            category: "claim/capability/calendar-mcp/list_accounts",
            content: "list_accounts cannot return more than 10 entries",
            id: "old-1"));

        var detector = NewDetector(memory);
        var incoming = NewEntry(
            category: "claim/capability/calendar-mcp/get_calendar_events",
            content: "get_calendar_events does support timeZone",
            id: "new-1");

        var resolution = await detector.ResolveAsync(incoming);

        Assert.IsFalse(resolution.HasContradiction,
            "Different (server, tool) pairs are different rule subjects.");
    }

    [TestMethod]
    public async Task ResolveAsync_CapabilityClaim_UserCorrectionExists_IncomingMarkedSuperseded()
    {
        var memory = new RecordingMemory();
        memory.Existing.Add(NewEntry(
            category: "claim/capability/calendar-mcp/get_calendar_events",
            content: "wrapper does pass arguments — verified manually",
            id: "user-correction-1",
            tags: ["correction"]));

        var detector = NewDetector(memory);
        var incoming = NewEntry(
            category: "claim/capability/calendar-mcp/get_calendar_events",
            content: "wrapper cannot pass arguments to get_calendar_events",
            id: "agent-self-1");

        var resolution = await detector.ResolveAsync(incoming);

        Assert.IsTrue(resolution.HasContradiction);
        Assert.AreEqual("user-correction-1", resolution.IncomingSupersededBy,
            "User-correction should win even when the incoming claim was saved later.");
        Assert.AreEqual(0, resolution.ExistingIdsToSupersede.Count);
    }

    [TestMethod]
    public async Task ResolveAsync_CapabilityClaim_SkipsAlreadySupersededExistingEntries()
    {
        var memory = new RecordingMemory();
        memory.Existing.Add(NewEntry(
            category: "claim/capability/calendar-mcp/get_calendar_events",
            content: "wrapper cannot pass arguments",
            id: "old-1") with { SupersededBy = "even-older-winner" });

        var detector = NewDetector(memory);
        var incoming = NewEntry(
            category: "claim/capability/calendar-mcp/get_calendar_events",
            content: "wrapper does pass arguments",
            id: "new-1");

        var resolution = await detector.ResolveAsync(incoming);

        Assert.IsFalse(resolution.HasContradiction,
            "Already-superseded entries should not appear as contradiction candidates.");
    }

    [TestMethod]
    public async Task ResolveAsync_Feedback_OppositeDirective_NewerWins()
    {
        var memory = new RecordingMemory();
        memory.Existing.Add(NewEntry(
            category: "feedback/from-agent/style",
            content: "Always use bullet points for status reports",
            id: "old-1"));

        var detector = NewDetector(memory);
        var incoming = NewEntry(
            category: "feedback/from-agent/style",
            content: "Never use bullet points for status reports",
            id: "new-1");

        var resolution = await detector.ResolveAsync(incoming);

        Assert.IsTrue(resolution.HasContradiction);
        CollectionAssert.AreEqual(new[] { "old-1" }, resolution.ExistingIdsToSupersede.ToArray());
    }

    [TestMethod]
    public async Task ResolveAsync_Feedback_UserCorrectionWins_OverAgentSelf()
    {
        var memory = new RecordingMemory();
        memory.Existing.Add(NewEntry(
            category: "feedback/from-user/style",
            content: "Always use bullet points for status reports",
            id: "user-correction-1"));

        var detector = NewDetector(memory);
        var incoming = NewEntry(
            category: "feedback/from-agent/style",
            content: "Never use bullet points for status reports",
            id: "agent-self-1");

        var resolution = await detector.ResolveAsync(incoming);

        Assert.IsTrue(resolution.HasContradiction);
        Assert.AreEqual("user-correction-1", resolution.IncomingSupersededBy);
    }

    [TestMethod]
    public async Task ResolveAsync_Feedback_AmbiguousMultipleMatches_DefersToDreamSweep()
    {
        var memory = new RecordingMemory();
        memory.Existing.Add(NewEntry(
            category: "feedback/from-agent/style",
            content: "Always use bullet points for status reports",
            id: "old-1"));
        memory.Existing.Add(NewEntry(
            category: "feedback/from-agent/style",
            content: "Always use bullet points for status reports as default",
            id: "old-2"));

        var detector = NewDetector(memory);
        var incoming = NewEntry(
            category: "feedback/from-agent/style",
            content: "Never use bullet points for status reports",
            id: "new-1");

        var resolution = await detector.ResolveAsync(incoming);

        Assert.IsFalse(resolution.HasContradiction,
            "Multiple non-correction candidates is ambiguous — Phase 3 design defers these to the dream sweep.");
    }

    [TestMethod]
    public async Task ResolveAsync_Feedback_DifferentSubject_NoContradiction()
    {
        var memory = new RecordingMemory();
        memory.Existing.Add(NewEntry(
            category: "feedback/from-agent/style",
            content: "Always use bullet points for status reports",
            id: "old-1"));

        var detector = NewDetector(memory);
        var incoming = NewEntry(
            category: "feedback/from-agent/scheduling",
            content: "Never schedule meetings on Friday afternoons",
            id: "new-1");

        var resolution = await detector.ResolveAsync(incoming);

        Assert.IsFalse(resolution.HasContradiction,
            "Different rule subjects (style vs scheduling) are not a contradiction.");
    }

    [TestMethod]
    public async Task ResolveAsync_Feedback_AmbiguousValence_Skipped()
    {
        var memory = new RecordingMemory();
        memory.Existing.Add(NewEntry(
            category: "feedback/from-agent/style",
            content: "Always use bullet points for status reports",
            id: "old-1"));

        var detector = NewDetector(memory);
        var incoming = NewEntry(
            category: "feedback/from-agent/style",
            content: "Bullet points format for status reports", // no clear directive
            id: "new-1");

        var resolution = await detector.ResolveAsync(incoming);

        Assert.IsFalse(resolution.HasContradiction);
    }

    // --- helpers --------------------------------------------------------------

    private static MemoryContradictionDetector NewDetector(ILongTermMemory memory) =>
        new(memory, NullLogger<MemoryContradictionDetector>.Instance);

    private static MemoryEntry NewEntry(
        string category,
        string content,
        string id,
        IReadOnlyList<string>? tags = null) =>
        new(
            Id: id,
            Content: content,
            Category: category,
            Tags: tags ?? [],
            CreatedAt: DateTimeOffset.UtcNow);

    private sealed class RecordingMemory : ILongTermMemory
    {
        public List<MemoryEntry> Existing { get; } = new();
        public int SearchCallCount { get; private set; }

        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            // Simulate the file store: filter by category prefix, hide superseded by default.
            IEnumerable<MemoryEntry> q = Existing;
            if (criteria.Category is not null)
            {
                var cat = criteria.Category;
                q = q.Where(e => e.Category is not null
                    && (string.Equals(e.Category, cat, StringComparison.OrdinalIgnoreCase)
                        || e.Category.StartsWith(cat + "/", StringComparison.OrdinalIgnoreCase)));
            }
            if (!criteria.IncludeSuperseded)
                q = q.Where(e => e.SupersededBy is null);
            return Task.FromResult<IReadOnlyList<MemoryEntry>>(q.Take(criteria.MaxResults).ToList());
        }

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Existing.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)));

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            Existing.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
