using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class FileRepairTicketStoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-repairticket-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task SaveAsync_NewTicket_PersistsAndRoundTrips()
    {
        var store = CreateStore();
        var ticket = NewOpenTicket("ticket-1");

        await store.SaveAsync(ticket);

        var loaded = await store.GetAsync("ticket-1");
        Assert.IsNotNull(loaded);
        Assert.AreEqual("ticket-1", loaded!.Id);
        Assert.AreEqual(RepairTarget.WorkingMemoryEvict, loaded.Target);
        Assert.AreEqual(RepairStatus.Open, loaded.Status);
        Assert.AreEqual("calendar-mcp|get_calendar_events|timeZone", loaded.PatternKey);
    }

    [TestMethod]
    public async Task SaveAsync_ExistingTicket_OverwritesAtomically()
    {
        var store = CreateStore();
        var ticket = NewOpenTicket("ticket-1");

        await store.SaveAsync(ticket);
        await store.SaveAsync(ticket with { Status = RepairStatus.Resolved });

        var loaded = await store.GetAsync("ticket-1");
        Assert.IsNotNull(loaded);
        Assert.AreEqual(RepairStatus.Resolved, loaded!.Status);
        // No leftover .tmp file
        Assert.AreEqual(0, Directory.EnumerateFiles(_tempDir, "*.tmp").Count());
    }

    [TestMethod]
    public async Task ListOpenAsync_FiltersResolvedAndEscalated()
    {
        var store = CreateStore();
        await store.SaveAsync(NewOpenTicket("open-1"));
        await store.SaveAsync(NewOpenTicket("inprogress-1") with { Status = RepairStatus.InProgress });
        await store.SaveAsync(NewOpenTicket("resolved-1") with { Status = RepairStatus.Resolved });
        await store.SaveAsync(NewOpenTicket("escalated-1") with { Status = RepairStatus.Escalated });

        var open = await store.ListOpenAsync();
        var ids = open.Select(t => t.Id).OrderBy(s => s).ToArray();

        CollectionAssert.AreEqual(new[] { "inprogress-1", "open-1" }, ids);
    }

    [TestMethod]
    public async Task ListAsync_OrdersByUpdatedAtDescending()
    {
        var store = CreateStore();
        var t0 = DateTimeOffset.UtcNow;
        await store.SaveAsync(NewOpenTicket("a") with { UpdatedAt = t0.AddMinutes(-10) });
        await store.SaveAsync(NewOpenTicket("b") with { UpdatedAt = t0 });
        await store.SaveAsync(NewOpenTicket("c") with { UpdatedAt = t0.AddMinutes(-5) });

        var all = await store.ListAsync();
        CollectionAssert.AreEqual(new[] { "b", "c", "a" }, all.Select(x => x.Id).ToArray());
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesTicket_NoOpWhenMissing()
    {
        var store = CreateStore();
        await store.SaveAsync(NewOpenTicket("x"));

        await store.DeleteAsync("x");
        Assert.IsNull(await store.GetAsync("x"));

        // Second delete must not throw.
        await store.DeleteAsync("x");
    }

    [TestMethod]
    public async Task GetAsync_MissingTicket_ReturnsNull()
    {
        var store = CreateStore();
        Assert.IsNull(await store.GetAsync("never-existed"));
    }

    [TestMethod]
    public async Task SaveAsync_RejectsPathTraversal()
    {
        var store = CreateStore();
        var t = NewOpenTicket("../escape");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(t));
    }

    private FileRepairTicketStore CreateStore() => new(
        Options.Create(new RepairTicketOptions { BasePath = _tempDir }),
        Options.Create(new AgentProfileOptions()),
        NullLogger<FileRepairTicketStore>.Instance);

    private static RepairTicket NewOpenTicket(string id)
    {
        var change = JsonDocument.Parse("""{ "keyPrefix": "claim/capability/calendar-mcp/get_calendar_events" }""").RootElement;
        var verifyArgs = JsonDocument.Parse("""{ "timeZone": "America/Chicago" }""").RootElement;

        return new RepairTicket(
            Id: id,
            PatternKey: "calendar-mcp|get_calendar_events|timeZone",
            Target: RepairTarget.WorkingMemoryEvict,
            Change: change,
            Verify: new VerifyShape(
                Server: "calendar-mcp",
                Tool: "get_calendar_events",
                Arguments: verifyArgs,
                Expect: new VerifyExpectation(VerifyExpectationKind.Success)),
            Attempts: [],
            Status: RepairStatus.Open,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }
}
