using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Agent.McpBridge.Tasks;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>The background-task ledger (#658): versioned, settles on delivery, purged a day later.</summary>
[TestClass]
public class McpTaskLedgerTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rockbot-task-ledger-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string LedgerPath => Path.Combine(_dir, "mcp", "tasks.json");

    private static McpTaskEntry Entry(string id = "t_1") => new()
    {
        TaskId = id,
        ServerTaskId = "server-" + id,
        SessionId = "session/test",
        Server = "research",
        Tool = "research",
        Arguments = """{"question":"mercury"}""",
        StartedAt = DateTimeOffset.UtcNow,
        Deadline = DateTimeOffset.UtcNow.AddHours(1),
    };

    [TestMethod]
    public async Task AnEntryIsWrittenBeforeAddReturns_WithTheVersion()
    {
        var ledger = new McpTaskLedger(LedgerPath, NullLogger.Instance);

        await ledger.AddAsync(Entry());

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(LedgerPath));
        Assert.AreEqual(McpTaskLedger.CurrentVersion, doc.RootElement.GetProperty("version").GetInt32());
        var entry = doc.RootElement.GetProperty("entries").EnumerateArray().Single();
        Assert.AreEqual("t_1", entry.GetProperty("taskId").GetString());
        Assert.AreEqual("server-t_1", entry.GetProperty("serverTaskId").GetString());
        Assert.AreEqual("running", entry.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task AnEndedTask_StaysOpenUntilDelivered_ThenIsPurgedADayLater()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var ledger = new McpTaskLedger(LedgerPath, NullLogger.Instance, clock);
        await ledger.AddAsync(Entry());

        await ledger.EndAsync("t_1", McpTaskEntryStatus.Completed, null);
        var ended = (await ledger.GetAsync("t_1"))!;
        Assert.AreEqual(McpTaskEntryStatus.Completed, ended.Status);
        Assert.IsFalse(ended.IsSettled, "not settled until its session has it");

        await ledger.MarkDeliveredAsync("t_1");
        Assert.IsTrue((await ledger.GetAsync("t_1"))!.IsSettled);

        clock.Advance(TimeSpan.FromHours(25));
        await ledger.AddAsync(Entry("t_2"));

        var reopened = new McpTaskLedger(LedgerPath, NullLogger.Instance, clock);
        Assert.AreEqual("t_2", (await reopened.ReadAllAsync()).Single().TaskId);
    }

    [TestMethod]
    public async Task AnUnreadableLedger_IsKeptAside_AndTheLedgerStartsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
        await File.WriteAllTextAsync(LedgerPath, "{ not json");

        var ledger = new McpTaskLedger(LedgerPath, NullLogger.Instance);

        Assert.AreEqual(0, (await ledger.ReadAllAsync()).Count);
        Assert.IsTrue(File.Exists(LedgerPath + ".corrupt"));
    }

    [TestMethod]
    public async Task AWriteWaitsOutAReaderHoldingTheFileOpen()
    {
        var ledger = new McpTaskLedger(LedgerPath, NullLogger.Instance);
        await ledger.AddAsync(Entry("t_1"));

        // On Windows the rename over the ledger fails while a reader has it open.
        Task write;
        using (new FileStream(LedgerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            write = ledger.AddAsync(Entry("t_2"));
            await Task.Delay(100);
        }

        await write.WaitAsync(TimeSpan.FromSeconds(5));
        var reopened = new McpTaskLedger(LedgerPath, NullLogger.Instance);
        Assert.AreEqual(2, (await reopened.ReadAllAsync()).Count);
    }

    [TestMethod]
    public async Task UnknownPropertiesAreTolerated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
        await File.WriteAllTextAsync(LedgerPath,
            """{"version":1,"entries":[{"taskId":"t_9","serverTaskId":"s","sessionId":"session/x","status":"running","someFutureField":42}]}""");

        var ledger = new McpTaskLedger(LedgerPath, NullLogger.Instance);

        Assert.AreEqual("s", (await ledger.GetAsync("t_9"))!.ServerTaskId);
    }
}
