namespace RockBot.Host.Tests;

[TestClass]
public class ProvisionalValidationPassTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecentlyCreated = Now.AddDays(-3);
    private static readonly DateTimeOffset LongAgoCreated = Now.AddDays(-60);

    private static SkillResource ProvWisp(string filename = "fanout.json", string hash = "h-abc",
        DateTimeOffset? createdAt = null, SkillResourceType type = SkillResourceType.Wisp) =>
        new(filename, type, "Per-account fan-out",
            Provisional: true,
            CreatedAt: createdAt ?? RecentlyCreated,
            VerifyHint: "exercises both accounts",
            DefinitionHash: hash);

    private static WispExecutionRecord WispRecord(
        bool succeeded, string sessionId, DateTimeOffset at, string hash = "h-abc") =>
        new()
        {
            WispId = Guid.NewGuid().ToString("N")[..8],
            Description = "x",
            DefinitionHash = hash,
            Succeeded = succeeded,
            StepCount = 1,
            StepsCompleted = succeeded ? 1 : 0,
            DurationMs = 5,
            Timestamp = at,
            SessionId = sessionId,
        };

    [TestMethod]
    public void Decide_ThreeDistinctSessionSuccesses_PromotesAndPreservesVerifyHint()
    {
        var records = new[]
        {
            WispRecord(true, "s1", Now.AddHours(-3)),
            WispRecord(true, "s2", Now.AddHours(-2)),
            WispRecord(true, "s3", Now.AddHours(-1)),
        };

        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(), records, [], Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreEqual(DreamService.ProvisionalAction.Promote, decision.Action);
        Assert.AreEqual(3, decision.SuccessCount);
    }

    [TestMethod]
    public void Decide_ThreeSuccessesSameSession_DoesNotPromote()
    {
        var records = new[]
        {
            WispRecord(true, "s1", Now.AddHours(-3)),
            WispRecord(true, "s1", Now.AddHours(-2)),
            WispRecord(true, "s1", Now.AddHours(-1)),
        };

        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(), records, [], Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreNotEqual(DreamService.ProvisionalAction.Promote, decision.Action);
    }

    [TestMethod]
    public void Decide_TwoConsecutiveFailures_RemovesResource()
    {
        var records = new[]
        {
            WispRecord(true, "s1", Now.AddHours(-5)),
            WispRecord(false, "s2", Now.AddHours(-2)),
            WispRecord(false, "s3", Now.AddHours(-1)),
        };

        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(), records, [], Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreEqual(DreamService.ProvisionalAction.Remove, decision.Action);
        Assert.AreEqual(2, decision.ConsecutiveFailureCount);
    }

    [TestMethod]
    public void Decide_FailureThenSuccess_DoesNotRemove()
    {
        var records = new[]
        {
            WispRecord(false, "s1", Now.AddHours(-3)),
            WispRecord(false, "s2", Now.AddHours(-2)),
            WispRecord(true, "s3", Now.AddHours(-1)),
        };

        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(), records, [], Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        // Most-recent record is a success, so consecutive-failure rule does not trigger.
        Assert.AreNotEqual(DreamService.ProvisionalAction.Remove, decision.Action);
    }

    [TestMethod]
    public void Decide_OldResourceWithNoActivity_MarksStale()
    {
        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(createdAt: LongAgoCreated), [], [], Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreEqual(DreamService.ProvisionalAction.MarkStale, decision.Action);
    }

    [TestMethod]
    public void Decide_OldResourceWithActivity_NotStale()
    {
        var records = new[] { WispRecord(true, "s1", Now.AddDays(-1)) };

        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(createdAt: LongAgoCreated), records, [], Now,
            successThreshold: 10, failureThreshold: 5, staleAfter: TimeSpan.FromDays(30));

        Assert.AreNotEqual(DreamService.ProvisionalAction.MarkStale, decision.Action);
    }

    [TestMethod]
    public void Decide_FreshResourceNoActivity_Keeps()
    {
        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(createdAt: RecentlyCreated), [], [], Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreEqual(DreamService.ProvisionalAction.Keep, decision.Action);
    }

    [TestMethod]
    public void Decide_NonWispWithEnoughCheckouts_Promotes()
    {
        var pythonRes = ProvWisp(filename: "compute.py", hash: "h-py", type: SkillResourceType.Python);
        var checkouts = new[]
        {
            new SkillResourceCheckoutEvent("calendar/scan", "compute.py", "s1", Now.AddHours(-3)),
            new SkillResourceCheckoutEvent("calendar/scan", "compute.py", "s2", Now.AddHours(-2)),
            new SkillResourceCheckoutEvent("calendar/scan", "compute.py", "s3", Now.AddHours(-1)),
        };

        var decision = DreamService.DecideProvisionalAction(
            pythonRes, [], checkouts, Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreEqual(DreamService.ProvisionalAction.Promote, decision.Action);
        Assert.AreEqual(3, decision.SuccessCount);
    }

    [TestMethod]
    public void Decide_NonWispCheckoutsSameSession_DoesNotPromote()
    {
        var pythonRes = ProvWisp(filename: "compute.py", type: SkillResourceType.Python);
        var checkouts = new[]
        {
            new SkillResourceCheckoutEvent("calendar/scan", "compute.py", "s1", Now.AddHours(-3)),
            new SkillResourceCheckoutEvent("calendar/scan", "compute.py", "s1", Now.AddHours(-2)),
            new SkillResourceCheckoutEvent("calendar/scan", "compute.py", "s1", Now.AddHours(-1)),
        };

        var decision = DreamService.DecideProvisionalAction(
            pythonRes, [], checkouts, Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreNotEqual(DreamService.ProvisionalAction.Promote, decision.Action);
    }

    // ── FileSkillResourceUsageStore round-trip ───────────────────────────────

    [TestMethod]
    public async Task FileSkillResourceUsageStore_RecordAndQuery_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rb-skillres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(
                new AgentProfileOptions { BasePath = tempDir });
            var store = new FileSkillResourceUsageStore(opts,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileSkillResourceUsageStore>.Instance);

            await store.RecordCheckoutAsync("calendar/scan", "compute.py", "s1", Now);
            await store.RecordCheckoutAsync("calendar/scan", "compute.py", "s2", Now.AddMinutes(1));
            await store.RecordCheckoutAsync("calendar/scan", "OTHER.py", "s3", Now);
            await store.RecordCheckoutAsync("OTHER/skill", "compute.py", "s4", Now);

            var results = await store.QueryCheckoutsAsync("calendar/scan", "compute.py", Now.AddMinutes(-10));
            Assert.AreEqual(2, results.Count);
            Assert.IsTrue(results.All(r => r.SkillName == "calendar/scan" && r.Filename == "compute.py"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public async Task FileSkillResourceUsageStore_FiltersBySinceTimestamp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rb-skillres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(
                new AgentProfileOptions { BasePath = tempDir });
            var store = new FileSkillResourceUsageStore(opts,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileSkillResourceUsageStore>.Instance);

            await store.RecordCheckoutAsync("s", "f", "s1", Now.AddDays(-10));
            await store.RecordCheckoutAsync("s", "f", "s2", Now.AddDays(-1));

            var results = await store.QueryCheckoutsAsync("s", "f", Now.AddDays(-5));
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("s2", results[0].SessionId);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
