using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// The running usage totals behind the info views (#834): per day, model, workload and thread,
/// persisted across restarts.
/// </summary>
[TestClass]
public sealed class LlmUsageLedgerTests
{
    private string _dir = null!;
    private LlmCostEstimator _estimator = null!;
    private DateTimeOffset _now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rb-usage-ledger", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // No pricing file: the built-in table, where claude-haiku-5-5 is $0.10 in / $0.50 out per M.
        _estimator = new LlmCostEstimator(
            Options.Create(new LlmPricingOptions { ConfigPath = Path.Combine(_dir, "no-pricing.json") }),
            NullLogger<LlmCostEstimator>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _estimator.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string LedgerPath => Path.Combine(_dir, "usage-ledger.json");

    private LlmUsageLedger NewLedger(LlmUsageOptions? options = null) =>
        new(_estimator, options ?? new LlmUsageOptions(), LedgerPath, NullLogger<LlmUsageLedger>.Instance,
            clock: null, now: () => _now, startTimer: false);

    private static UsageDetails Usage(long input, long output, long cached = 0) =>
        new() { InputTokenCount = input, OutputTokenCount = output, CachedInputTokenCount = cached };

    [TestMethod]
    public void Record_AddsUpTodayWithCostFromThePricingTable()
    {
        using var ledger = NewLedger();

        ledger.Record("session/blazor-session", "blazor-session", "claude-haiku-5-5", Usage(1_000_000, 1_000_000, cached: 400_000));
        ledger.Record("session/blazor-session", "blazor-session", "claude-haiku-5-5", Usage(1_000_000, 0));

        var report = ledger.GetReport(null);
        Assert.AreEqual("2026-11-15", report.Today);
        Assert.AreEqual(2, report.TodayTotals.Requests);
        Assert.AreEqual(2_000_000, report.TodayTotals.InputTokens);
        Assert.AreEqual(400_000, report.TodayTotals.CachedInputTokens);
        Assert.AreEqual(1_000_000, report.TodayTotals.OutputTokens);
        // 600K uncached at $0.10 + 400K cached at $0.01 + 1M out at $0.50, then 1M in at $0.10.
        Assert.AreEqual(0.06 + 0.004 + 0.50 + 0.10, report.TodayTotals.CostUsd, 1e-9);
        Assert.AreEqual(0, report.TodayTotals.UnpricedRequests);
        Assert.AreEqual("UTC", report.TimeZone);
        Assert.AreEqual(_now, report.Since);
    }

    [TestMethod]
    public void Record_IgnoresRequestsWithoutTokens()
    {
        using var ledger = NewLedger();

        ledger.Record("s", "s", "claude-haiku-5-5", null);
        ledger.Record("s", "s", "claude-haiku-5-5", Usage(0, 0));

        Assert.AreEqual(0, ledger.GetReport(null).TodayTotals.Requests);
        Assert.IsNull(ledger.GetReport(null).Since);
    }

    [TestMethod]
    public void AModelNoRowPrices_IsCountedAsUnpriced()
    {
        using var ledger = NewLedger();

        ledger.Record("s", "s", "house-model-7", Usage(1000, 100));

        var today = ledger.GetReport(null).TodayTotals;
        Assert.AreEqual(1, today.UnpricedRequests);
        Assert.AreEqual(0, today.CostUsd);
    }

    [TestMethod]
    public void ARequestIsChargedToItsThread_AndItsWorkloadKind()
    {
        using var ledger = NewLedger();

        // The user's own turn, then a subagent it started (charged to the same thread), then a patrol.
        ledger.Record("session/blazor-session", "blazor-session", "claude-haiku-5-5", Usage(1000, 100));
        ledger.Record("subagent-abc", "blazor-session", "claude-haiku-5-5", Usage(2000, 200));
        ledger.Record("patrol/dream", null, "gpt-5.6-luna", Usage(5000, 500));

        var report = ledger.GetReport("blazor-session");
        Assert.AreEqual(2, report.Thread!.Totals.Requests, "the subagent counts toward the thread that started it");
        Assert.AreEqual(3000, report.Thread.Totals.InputTokens);

        CollectionAssert.AreEquivalent(new[] { "session", "subagent", "patrol" }, report.Kinds30Days.Select(k => k.Kind).ToArray());
        CollectionAssert.AreEquivalent(new[] { "claude-haiku-5-5", "gpt-5.6-luna" }, report.Models30Days.Select(m => m.ModelId).ToArray());
        Assert.IsNull(ledger.GetReport("someone-else").Thread);
    }

    [TestMethod]
    public void Ranges_CountTodayAndTheDaysBeforeIt()
    {
        using var ledger = NewLedger();
        void At(int daysAgo, long input)
        {
            _now = new DateTimeOffset(2026, 11, 15, 18, 0, 0, TimeSpan.Zero).AddDays(-daysAgo);
            ledger.Record("s", "s", "claude-haiku-5-5", Usage(input, 0));
        }

        At(0, 1);
        At(6, 10);    // still in the last 7 days
        At(7, 100);   // in the last 30, not the last 7
        At(29, 1000); // the 30th day
        At(30, 10000);// outside
        _now = new DateTimeOffset(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);

        var report = ledger.GetReport(null);
        Assert.AreEqual(1, report.TodayTotals.InputTokens);
        Assert.AreEqual(11, report.Last7Days.InputTokens);
        Assert.AreEqual(1111, report.Last30Days.InputTokens);
        CollectionAssert.AreEqual(new[] { "2026-11-15", "2026-11-09", "2026-11-08", "2026-10-17" },
            report.Days.Select(d => d.Date).ToArray(), "days with usage in the last 30, newest first");
    }

    [TestMethod]
    public void Flush_PersistsAcrossARestart_AndPrunesPastTheRetention()
    {
        using (var ledger = NewLedger(new LlmUsageOptions { RetentionDays = 10 }))
        {
            _now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
            ledger.Record("s", "old-thread", "claude-haiku-5-5", Usage(500, 0));
            _now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
            ledger.Record("s", "blazor-session", "claude-haiku-5-5", Usage(1000, 100));
            ledger.Flush();
        }

        var json = JsonDocument.Parse(File.ReadAllText(LedgerPath)).RootElement;
        Assert.AreEqual(LlmUsageLedgerFile.CurrentSchemaVersion, json.GetProperty("schemaVersion").GetInt32());
        Assert.IsFalse(json.GetProperty("days").TryGetProperty("2026-11-01", out _), "a day past the retention is pruned");
        Assert.IsFalse(json.GetProperty("threads").TryGetProperty("old-thread", out _), "so is a thread idle past it");

        using var reloaded = NewLedger();
        var report = reloaded.GetReport("blazor-session");
        Assert.AreEqual(1000, report.TodayTotals.InputTokens);
        Assert.AreEqual(1, report.Thread!.Totals.Requests);
        Assert.IsNotNull(report.Since);
    }

    [TestMethod]
    public void Flush_KeepsOnlyTheMostRecentlyActiveThreads()
    {
        using (var ledger = NewLedger(new LlmUsageOptions { MaxThreads = 2 }))
        {
            foreach (var (thread, minute) in new[] { ("a", 1), ("b", 2), ("c", 3) })
            {
                _now = new DateTimeOffset(2026, 11, 15, 12, minute, 0, TimeSpan.Zero);
                ledger.Record("s", thread, "claude-haiku-5-5", Usage(10, 1));
            }
            ledger.Flush();
        }

        using var reloaded = NewLedger();
        Assert.IsNull(reloaded.GetReport("a").Thread);
        Assert.IsNotNull(reloaded.GetReport("b").Thread);
        Assert.IsNotNull(reloaded.GetReport("c").Thread);
    }

    [TestMethod]
    public void ACorruptFile_IsSetAsideAndANewLedgerStarts()
    {
        File.WriteAllText(LedgerPath, "{ not json");

        using var ledger = NewLedger();

        Assert.AreEqual(0, ledger.GetReport(null).TodayTotals.Requests);
        Assert.IsTrue(File.Exists(LedgerPath + ".corrupt"));
    }

    [TestMethod]
    public async Task TheAccountingClient_RecordsEachRequestIntoTheLedger_ChargedToTheProgressThread()
    {
        using var ledger = NewLedger();
        var sut = new RequestAccountingChatClient(new FixedUsageClient(), NullLogger<RequestAccountingChatClient>.Instance,
            "Balanced", ledger);

        using (RequestAccountingContext.Begin("subagent-t1", NullLogger.Instance))
        using (ToolProgressNotifier.SetContext(new ToolProgressContext
               {
                   SessionId = "session/blazor-session", AgentName = "subagent-t1", ReplyTo = "x", ThreadId = "blazor-session",
               }))
        {
            await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        }

        var report = ledger.GetReport("blazor-session");
        Assert.AreEqual(1, report.Thread!.Totals.Requests);
        Assert.AreEqual("subagent", report.Kinds30Days.Single().Kind);
        Assert.AreEqual("claude-haiku-5-5", report.Models30Days.Single().ModelId);
    }

    private sealed class FixedUsageClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                Usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 50 },
                ModelId = "claude-haiku-5-5",
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
