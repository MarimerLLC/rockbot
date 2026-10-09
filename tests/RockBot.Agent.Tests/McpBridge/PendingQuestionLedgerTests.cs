using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using RockBot.Agent.McpBridge.Handback;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.Tests.McpBridge;

[TestClass]
public class PendingQuestionLedgerTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rockbot-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string LedgerPath => Path.Combine(_dir, "mcp", "pending-questions.json");

    private static PendingQuestionEntry Entry(string id, string status = PendingQuestionStatus.Pending) => new()
    {
        QuestionId = id,
        Status = status,
        SessionId = "session/test",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        Call = new PendingCallContext { Server = "s", Tool = "t" },
        Question = new PendingQuestionText { Message = "Which?" },
    };

    [TestMethod]
    public async Task Entries_SurviveANewInstance_WithAVersion()
    {
        await new PendingQuestionLedger(LedgerPath, NullLogger.Instance).AddAsync(Entry("q_1"));

        var reread = await new PendingQuestionLedger(LedgerPath, NullLogger.Instance).ReadAllAsync();

        Assert.AreEqual("q_1", reread.Single().QuestionId);
        StringAssert.Contains(File.ReadAllText(LedgerPath), "\"version\": 1");
    }

    [TestMethod]
    public async Task AWrite_LeavesNoTemporaryFilesBehind()
    {
        var ledger = new PendingQuestionLedger(LedgerPath, NullLogger.Instance);
        await ledger.AddAsync(Entry("q_1"));
        await ledger.SettleAsync("q_1", PendingQuestionStatus.Answered);

        CollectionAssert.AreEqual(new[] { "pending-questions.json" },
            Directory.GetFiles(Path.GetDirectoryName(LedgerPath)!).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task AFailedWrite_LeavesThePreviousLedgerIntact()
    {
        var ledger = new PendingQuestionLedger(LedgerPath, NullLogger.Instance);
        await ledger.AddAsync(Entry("q_1"));
        var before = File.ReadAllText(LedgerPath);

        // The ledger file locked open: the rename over it fails on Windows, and on Linux the
        // directory is made read-only so the temporary file can't be created.
        var directory = Path.GetDirectoryName(LedgerPath)!;
        using (var locked = OperatingSystem.IsWindows()
                   ? new FileStream(LedgerPath, FileMode.Open, FileAccess.Read, FileShare.None)
                   : null)
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            Exception? failure = null;
            try
            {
                await ledger.AddAsync(Entry("q_2"));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            if (failure is null)
                Assert.Inconclusive("This environment let the write through (running as root?), so there was no failure to observe.");
            Assert.IsTrue(failure is IOException or UnauthorizedAccessException, failure.ToString());
        }

        Assert.AreEqual(before, File.ReadAllText(LedgerPath));
        CollectionAssert.AreEqual(new[] { "q_1" }, (await ledger.ReadAllAsync()).Select(e => e.QuestionId).ToArray(),
            "a failed write must not leave the ledger's memory ahead of its file");
    }

    [TestMethod]
    public async Task SettledEntries_ArePurgedADayAfterTheySettle_PendingOnesNever()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var ledger = new PendingQuestionLedger(LedgerPath, NullLogger.Instance, time);
        await ledger.AddAsync(Entry("q_answered"));
        await ledger.AddAsync(Entry("q_pending"));
        await ledger.SettleAsync("q_answered", PendingQuestionStatus.Answered);

        time.Advance(TimeSpan.FromHours(23));
        await ledger.AddAsync(Entry("q_new"));
        Assert.AreEqual(3, (await ledger.ReadAllAsync()).Count);

        time.Advance(TimeSpan.FromHours(2));
        await ledger.AddAsync(Entry("q_newer"));

        CollectionAssert.AreEquivalent(new[] { "q_pending", "q_new", "q_newer" },
            (await ledger.ReadAllAsync()).Select(e => e.QuestionId).ToArray());
    }

    [TestMethod]
    public async Task SettlingTwice_KeepsTheFirstOutcome()
    {
        var ledger = new PendingQuestionLedger(LedgerPath, NullLogger.Instance);
        await ledger.AddAsync(Entry("q_1"));

        await ledger.SettleAsync("q_1", PendingQuestionStatus.Expired, "late");
        await ledger.SettleAsync("q_1", PendingQuestionStatus.Answered);

        var entry = (await ledger.GetAsync("q_1"))!;
        Assert.AreEqual(PendingQuestionStatus.Expired, entry.Status);
        Assert.AreEqual("late", entry.Reason);
    }

    [TestMethod]
    public async Task AnUnreadableLedger_IsKeptAside_AndTheBridgeStartsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
        File.WriteAllText(LedgerPath, "{ not json");

        var ledger = new PendingQuestionLedger(LedgerPath, NullLogger.Instance);

        Assert.AreEqual(0, (await ledger.ReadAllAsync()).Count);
        Assert.AreEqual("{ not json", File.ReadAllText(LedgerPath + ".corrupt"));
    }

    [TestMethod]
    public async Task RemovingAServer_CancelsItsQuestions_AndLeavesOthers()
    {
        var ledger = new PendingQuestionLedger(LedgerPath, NullLogger.Instance);
        using var store = new PendingQuestionStore(ledger, maxPerSession: 5, maxTotal: 50, NullLogger.Instance);
        using var mail = Call(store, "mail");
        using var files = Call(store, "files");

        var (mailQuestion, _) = await store.OpenAsync(mail, Question("mail"), CancellationToken.None);
        var (filesQuestion, _) = await store.OpenAsync(files, Question("files"), CancellationToken.None);

        await store.CancelServerAsync("MAIL", "the MCP server \"mail\" was removed");

        Assert.IsTrue(mail.Cts.IsCancellationRequested, "the parked call is cancelled");
        Assert.AreEqual("the MCP server \"mail\" was removed", mail.AbortReason);
        Assert.IsNull((await mailQuestion!.Submission).Target, "nobody waits for an abandoned call");
        Assert.IsNull(store.Get(mailQuestion.QuestionId));
        Assert.AreEqual(PendingQuestionStatus.Cancelled, (await ledger.GetAsync(mailQuestion.QuestionId))!.Status);

        Assert.IsFalse(files.Cts.IsCancellationRequested);
        Assert.IsNotNull(store.Get(filesQuestion!.QuestionId));
    }

    [TestMethod]
    public async Task Shutdown_AbandonsCalls_ButLeavesTheirEntriesPendingForTheNextStart()
    {
        var ledger = new PendingQuestionLedger(LedgerPath, NullLogger.Instance);
        using var store = new PendingQuestionStore(ledger, maxPerSession: 5, maxTotal: 50, NullLogger.Instance);
        using var call = Call(store, "mail");
        var (question, _) = await store.OpenAsync(call, Question("mail"), CancellationToken.None);

        store.AbandonAllForShutdown();

        Assert.IsTrue(call.Cts.IsCancellationRequested);
        Assert.AreEqual(PendingQuestionStatus.Pending, (await ledger.GetAsync(question!.QuestionId))!.Status);
    }

    [TestMethod]
    public async Task TheBridgeWideCap_DeclinesInBand()
    {
        var ledger = new PendingQuestionLedger(LedgerPath, NullLogger.Instance);
        using var store = new PendingQuestionStore(ledger, maxPerSession: 5, maxTotal: 1, NullLogger.Instance);
        using var first = Call(store, "mail");
        using var second = Call(store, "mail", session: "session/other");

        Assert.IsNotNull((await store.OpenAsync(first, Question("mail"), CancellationToken.None)).Question);
        var (refused, reason) = await store.OpenAsync(second, Question("mail"), CancellationToken.None);

        Assert.IsNull(refused);
        StringAssert.Contains(reason, "limit 1");
    }

    private static HandbackCall Call(PendingQuestionStore store, string server, string session = "session/test") =>
        new(store, new HandbackCallInfo(server, "search", "Searches.", "{}", session, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30)),
            timeoutMs: 60_000, CancellationToken.None);

    private static McpHandbackQuestion Question(string server) =>
        new(server, new ElicitRequestParams { Message = "Which?" }, 1);
}
