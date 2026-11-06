using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Agent.McpBridge.Watches;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>The resource-watch ledger (#768): versioned, hashes not content, settles when its ending is delivered.</summary>
[TestClass]
public class McpWatchLedgerTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rockbot-watch-ledger-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string LedgerPath => Path.Combine(_dir, "mcp", "watches.json");

    private static McpWatchEntry Entry(string id = "w_1") => new()
    {
        WatchId = id,
        SessionId = "session/test",
        Server = "scheduler",
        ServerId = "srv-1",
        Uri = "scheduler://events/AB12/status",
        LastHash = "hash-1",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(14),
    };

    [TestMethod]
    public async Task AnEntryIsWrittenBeforeAddReturns_WithTheVersion()
    {
        var ledger = new McpWatchLedger(LedgerPath, NullLogger.Instance);

        await ledger.AddAsync(Entry());

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(LedgerPath));
        Assert.AreEqual(McpWatchLedger.CurrentVersion, doc.RootElement.GetProperty("version").GetInt32());
        var entry = doc.RootElement.GetProperty("entries").EnumerateArray().Single();
        Assert.AreEqual("w_1", entry.GetProperty("watchId").GetString());
        Assert.AreEqual("scheduler://events/AB12/status", entry.GetProperty("uri").GetString());
        Assert.AreEqual("hash-1", entry.GetProperty("lastHash").GetString());
        Assert.AreEqual(McpWatchEntryStatus.Active, entry.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task TheLastHash_SurvivesARestartOfTheLedger()
    {
        var ledger = new McpWatchLedger(LedgerPath, NullLogger.Instance);
        await ledger.AddAsync(Entry());
        await ledger.SetLastHashAsync("w_1", "hash-2");

        var reopened = new McpWatchLedger(LedgerPath, NullLogger.Instance);
        var entry = (await reopened.ReadAllAsync()).Single();
        Assert.AreEqual("hash-2", entry.LastHash);
        Assert.IsTrue(entry.IsActive);
        Assert.IsFalse(entry.IsSettled);
    }

    [TestMethod]
    public async Task AnEndingTheSessionMustHear_StaysOpenUntilDelivered()
    {
        var ledger = new McpWatchLedger(LedgerPath, NullLogger.Instance);
        await ledger.AddAsync(Entry());

        await ledger.EndAsync("w_1", "it expired", notify: true);
        var ended = (await ledger.GetAsync("w_1"))!;
        Assert.AreEqual(McpWatchEntryStatus.Ended, ended.Status);
        Assert.IsTrue(ended.NotifySession);
        Assert.IsFalse(ended.IsSettled, "a restart must still deliver it");

        await ledger.MarkSettledAsync("w_1");
        Assert.IsTrue((await ledger.GetAsync("w_1"))!.IsSettled);
    }

    [TestMethod]
    public async Task AnEndingTheAgentAskedFor_SettlesAtOnce()
    {
        var ledger = new McpWatchLedger(LedgerPath, NullLogger.Instance);
        await ledger.AddAsync(Entry());

        await ledger.EndAsync("w_1", "the agent stopped watching it", notify: false);

        var ended = (await ledger.GetAsync("w_1"))!;
        Assert.IsTrue(ended.IsSettled);
        Assert.AreEqual("the agent stopped watching it", ended.Reason);
    }
}
