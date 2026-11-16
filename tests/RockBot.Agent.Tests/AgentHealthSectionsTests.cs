using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Agent.McpBridge;
using RockBot.Host;

namespace RockBot.Agent.Tests;

/// <summary>
/// The agent-health introspection sections (#834, stage 4): what they read, and what they keep out
/// (credentials in connect errors, memory ids, the user's prompts).
/// </summary>
[TestClass]
public sealed class AgentHealthSectionsTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rb-health", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void DescribeConnectError_KeepsTheReason_ButNoTokenFromAUrl()
    {
        var ex = new InvalidOperationException("Connect failed",
            new HttpRequestException(
                "Response status code 401 from https://user:pa55@mcp.example.com/v1/mcp?api_key=SECRET123&x=1 (Unauthorized).\n  Retry later."));

        var text = McpBridgeService.DescribeConnectError(ex);

        Assert.AreEqual("Response status code 401 from https://mcp.example.com/v1/mcp (Unauthorized). Retry later.", text);
        Assert.IsFalse(text.Contains("SECRET", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("pa55", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DescribeConnectError_IsCappedAndNeverEmpty()
    {
        Assert.AreEqual(300, McpBridgeService.DescribeConnectError(new Exception(new string('x', 1000))).Length);
        Assert.AreEqual("TimeoutException", McpBridgeService.DescribeConnectError(new TimeoutException(" ")));
    }

    [TestMethod]
    [DataRow("research", false, "learned")]
    [DataRow("mcp/calendar/list-events", false, "mcp")]
    [DataRow("getting-started", true, "starter")]
    public void SkillSource(string name, bool codeOwned, string expected) =>
        Assert.AreEqual(expected, AgentHealthSections.SourceOf(new Skill(name, "", "", DateTimeOffset.UtcNow, CodeOwned: codeOwned)));

    [TestMethod]
    public async Task Memory_ReadsTheLatestSnapshot_WithoutEntryIds()
    {
        var older = new MemoryAuditSnapshot { SnapshotId = "s1", TakenAt = DateTimeOffset.Parse("2026-11-13T04:00:00Z"), Live = 100 };
        var latest = new MemoryAuditSnapshot
        {
            SnapshotId = "s2",
            TakenAt = DateTimeOffset.Parse("2026-11-14T04:00:00Z"),
            Live = 120,
            Archived = 30,
            CreatedSinceLast = 22,
            NetGrowthPerDay = 2.5,
            NearDupPairs = 3,
            Purge = new MemoryAuditPurgeOutlook(7, 4, 1),
            Status = MemoryAuditStatuses.Warning,
            Invariants = [new MemoryAuditInvariantViolation("chain-depth", "2 merge chains are deeper than 2", ["mem-secret-id"])],
            Eval = new MemoryAuditEvalSummary(DateTimeOffset.Parse("2026-11-09T05:00:00Z"), 10, 9, 0.9, new Dictionary<string, double>()),
        };
        await File.WriteAllLinesAsync(Path.Combine(_dir, MemoryAuditFiles.SnapshotsFile),
            [JsonSerializer.Serialize(older), JsonSerializer.Serialize(latest), ""]);

        var memory = await AgentHealthSections.MemoryAsync(_dir, NullLogger.Instance);

        Assert.AreEqual(latest.TakenAt, memory.AuditedAt);
        Assert.AreEqual("warning", memory.Status);
        Assert.AreEqual(120, memory.Live);
        Assert.AreEqual(4, memory.PurgeDueCount);
        Assert.AreEqual(0.9, memory.EvalSoundRate);
        Assert.AreEqual("2 merge chains are deeper than 2", memory.Findings.Single().Message);
        Assert.IsFalse(JsonSerializer.Serialize(memory).Contains("mem-secret-id", StringComparison.Ordinal),
            "a finding's entry ids point into the user's memories and stay out");
        Assert.IsFalse(memory.ConsolidationPaused);
    }

    [TestMethod]
    public async Task Memory_ReportsAPausedConsolidation_EvenBeforeAnyAudit()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, MemoryAuditFiles.ConsolidationPausedFile),
            """{ "reason": "3 entry(s) were hard-deleted outside the retention purge", "snapshotId": "s9", "pausedAt": "2026-11-14T04:01:00+00:00" }""");

        var memory = await AgentHealthSections.MemoryAsync(_dir, NullLogger.Instance);

        Assert.IsNull(memory.AuditedAt);
        Assert.IsTrue(memory.ConsolidationPaused);
        Assert.AreEqual("3 entry(s) were hard-deleted outside the retention purge", memory.ConsolidationPausedReason);
        Assert.AreEqual(DateTimeOffset.Parse("2026-11-14T04:01:00Z"), memory.ConsolidationPausedAt);
    }

    [TestMethod]
    public async Task Routing_IsTheLastWeeksGlobalStats_WithoutPrompts()
    {
        var log = new TierRoutingLogger(Options.Create(new AgentProfileOptions { BasePath = _dir }), NullLogger<TierRoutingLogger>.Instance);
        var now = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
        await log.AppendAsync(new TierRoutingEntry { Timestamp = now.AddDays(-8), Tier = ModelTier.High, Context = "user-message", PromptPreview = "too old" });
        await log.AppendAsync(new TierRoutingEntry { Timestamp = now.AddDays(-2), Tier = ModelTier.Balanced, Context = "user-message", PromptPreview = "my private question" });
        await log.AppendAsync(new TierRoutingEntry { Timestamp = now.AddDays(-1), Tier = ModelTier.Low, Context = "subagent", PromptPreview = "another" });

        var routing = await AgentHealthSections.RoutingAsync(log, now);

        Assert.AreEqual(AgentHealthSections.RoutingWindowDays, routing.WindowDays);
        Assert.AreEqual(2, routing.Total, "the entry older than the window is left out");
        Assert.AreEqual(1, routing.UserMessages);
        Assert.AreEqual(1, routing.Subagents);
        CollectionAssert.IsSubsetOf(new[] { "Balanced", "Low" }, routing.Tiers.Where(t => t.Count > 0).Select(t => t.Tier).ToArray());
        Assert.IsFalse(JsonSerializer.Serialize(routing).Contains("private question", StringComparison.Ordinal));
    }
}
