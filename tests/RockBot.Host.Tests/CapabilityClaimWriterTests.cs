using System.Text.Json;

namespace RockBot.Host.Tests;

[TestClass]
public class CapabilityClaimWriterTests
{
    [TestMethod]
    public async Task SaveCapabilityClaimAsync_BuildsConventionalCategoryAndAttachesVerifyShape()
    {
        var memory = new RecordingMemory();
        var writer = new CapabilityClaimWriter(memory);

        var claim = NewClaim();

        await writer.SaveCapabilityClaimAsync(claim);

        Assert.AreEqual(1, memory.Saved.Count);
        var saved = memory.Saved[0];
        Assert.AreEqual("claim/capability/calendar-mcp/get_calendar_events", saved.Category);
        Assert.IsNotNull(saved.Verify);
        Assert.AreEqual("calendar-mcp", saved.Verify!.Server);
        Assert.AreEqual("get_calendar_events", saved.Verify.Tool);
        Assert.AreEqual(VerifyExpectationKind.Success, saved.Verify.Expect.Kind);
        CollectionAssert.Contains((System.Collections.ICollection)saved.Tags, "capability-claim");
        CollectionAssert.Contains((System.Collections.ICollection)saved.Tags, "server:calendar-mcp");
        CollectionAssert.Contains((System.Collections.ICollection)saved.Tags, "tool:get_calendar_events");
    }

    [TestMethod]
    public async Task SaveCapabilityClaimAsync_DeterministicId_OverwritesIdenticalClaim()
    {
        var memory = new RecordingMemory();
        var writer = new CapabilityClaimWriter(memory);

        var claim = NewClaim();

        await writer.SaveCapabilityClaimAsync(claim);
        await writer.SaveCapabilityClaimAsync(claim);

        // Two saves of an identical claim — IDs match so the store sees the same Id twice.
        Assert.AreEqual(2, memory.Saved.Count, "Both saves are received by the store.");
        Assert.AreEqual(memory.Saved[0].Id, memory.Saved[1].Id, "Identical claims must share an ID so the store overwrites instead of accumulating.");
    }

    [TestMethod]
    public async Task SaveCapabilityClaimAsync_DifferentStatement_ProducesDistinctId()
    {
        var memory = new RecordingMemory();
        var writer = new CapabilityClaimWriter(memory);

        var first = NewClaim() with { Statement = "wrapper cannot pass arguments" };
        var second = NewClaim() with { Statement = "wrapper times out under fan-out" };

        await writer.SaveCapabilityClaimAsync(first);
        await writer.SaveCapabilityClaimAsync(second);

        Assert.AreNotEqual(memory.Saved[0].Id, memory.Saved[1].Id);
    }

    [TestMethod]
    public async Task SaveCapabilityClaimAsync_PopulatesEvidenceMetadata()
    {
        var memory = new RecordingMemory();
        var writer = new CapabilityClaimWriter(memory);

        var claim = NewClaim() with { Evidence = ["session a saw timeZone error", "session b saw same"] };

        await writer.SaveCapabilityClaimAsync(claim);

        var meta = memory.Saved[0].Metadata!;
        Assert.AreEqual("capability-claim", meta["kind"]);
        Assert.AreEqual("calendar-mcp", meta["server"]);
        Assert.AreEqual("get_calendar_events", meta["tool"]);
        Assert.AreEqual("2", meta["evidenceCount"]);
        StringAssert.Contains(meta["evidence"], "session a saw timeZone error");
        StringAssert.Contains(meta["evidence"], "session b saw same");
    }

    [TestMethod]
    public async Task SaveCapabilityClaimAsync_NullVerifyShape_Throws()
    {
        var memory = new RecordingMemory();
        var writer = new CapabilityClaimWriter(memory);

        var claim = new CapabilityClaim(
            Server: "calendar-mcp",
            Tool: "get_calendar_events",
            Statement: "wrapper cannot pass arguments",
            Verify: null!,
            Evidence: [],
            CreatedAt: DateTimeOffset.UtcNow);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => writer.SaveCapabilityClaimAsync(claim));
        Assert.AreEqual(0, memory.Saved.Count, "Invalid claims must not reach the store.");
    }

    [TestMethod]
    public async Task SaveCapabilityClaimAsync_FailureExpectationWithoutPattern_Throws()
    {
        var memory = new RecordingMemory();
        var writer = new CapabilityClaimWriter(memory);

        var claim = NewClaim() with
        {
            Verify = NewVerify() with
            {
                Expect = new VerifyExpectation(VerifyExpectationKind.FailureWithMessage, FailurePattern: null)
            }
        };

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => writer.SaveCapabilityClaimAsync(claim));
    }

    [TestMethod]
    public async Task SaveCapabilityClaimAsync_FailureExpectationWithEmptyPattern_Throws()
    {
        var memory = new RecordingMemory();
        var writer = new CapabilityClaimWriter(memory);

        var claim = NewClaim() with
        {
            Verify = NewVerify() with
            {
                Expect = new VerifyExpectation(VerifyExpectationKind.FailureWithMessage, FailurePattern: "")
            }
        };

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => writer.SaveCapabilityClaimAsync(claim));
    }

    [TestMethod]
    [DataRow("", "tool", "stmt")]
    [DataRow("server", "", "stmt")]
    [DataRow("server", "tool", "")]
    [DataRow("   ", "tool", "stmt")]
    public async Task SaveCapabilityClaimAsync_MissingRequiredFields_Throws(string server, string tool, string statement)
    {
        var memory = new RecordingMemory();
        var writer = new CapabilityClaimWriter(memory);

        var claim = new CapabilityClaim(
            Server: server,
            Tool: tool,
            Statement: statement,
            Verify: NewVerify(),
            Evidence: [],
            CreatedAt: DateTimeOffset.UtcNow);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => writer.SaveCapabilityClaimAsync(claim));
    }

    // --- helpers -------------------------------------------------------------

    private static CapabilityClaim NewClaim() => new(
        Server: "calendar-mcp",
        Tool: "get_calendar_events",
        Statement: "wrapper cannot pass arguments to get_calendar_events",
        Verify: NewVerify(),
        Evidence: ["initial observation"],
        CreatedAt: new DateTimeOffset(2026, 5, 8, 15, 0, 0, TimeSpan.Zero));

    private static VerifyShape NewVerify() => new(
        Server: "calendar-mcp",
        Tool: "get_calendar_events",
        Arguments: JsonDocument.Parse("""{"accountId":"x","timeZone":"America/Chicago","startDate":"2026-05-08","endDate":"2026-05-08"}""").RootElement,
        Expect: new VerifyExpectation(VerifyExpectationKind.Success));

    private sealed class RecordingMemory : ILongTermMemory
    {
        public List<MemoryEntry> Saved { get; } = new();

        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            Saved.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryEntry?>(Saved.FirstOrDefault(e => e.Id == id));

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            Saved.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
