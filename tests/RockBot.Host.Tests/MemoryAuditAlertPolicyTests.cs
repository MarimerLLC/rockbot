namespace RockBot.Host.Tests;

/// <summary>
/// When the audit speaks. A live deployment reported the same <c>warning</c> on every one of 23
/// consecutive runs; these pin down that only news is pushed, and that loss never goes quiet.
/// </summary>
[TestClass]
public class MemoryAuditAlertPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 4, 0, 0, TimeSpan.Zero);

    private const string Malformed = MemoryAuditInvariants.NoMalformedFiles;
    private const string Growth = MemoryAuditInvariants.NetGrowthThreshold;
    private const string HardDelete = MemoryAuditInvariants.NoHardDeleteOutsidePurge;

    [TestMethod]
    public void AHealthyRunSendsNothing()
    {
        var decision = Decide(Snapshot(), last: [Malformed], lastAt: Now.AddDays(-1));

        Assert.IsFalse(decision.Send);
        Assert.AreEqual(MemoryAuditAlertReason.None, decision.Reason);
        CollectionAssert.AreEqual(new[] { Malformed }, decision.Cleared.ToArray());
    }

    [TestMethod]
    public void TheFirstWarningIsSent()
    {
        var decision = Decide(Snapshot(Malformed), last: [], lastAt: null);

        Assert.IsTrue(decision.Send);
        Assert.AreEqual(MemoryAuditAlertReason.Changed, decision.Reason);
        CollectionAssert.AreEqual(new[] { Malformed }, decision.Added.ToArray());
    }

    [TestMethod]
    public void TheSameWarningInsideTheRepeatIntervalIsNotSentAgain()
    {
        var decision = Decide(Snapshot(Malformed), last: [Malformed], lastAt: Now.AddDays(-1));

        Assert.IsFalse(decision.Send);
        Assert.AreEqual(MemoryAuditAlertReason.None, decision.Reason);
    }

    [TestMethod]
    public void TheSameWarningIsRepeatedOnceTheIntervalHasPassed()
    {
        var decision = Decide(Snapshot(Malformed), last: [Malformed], lastAt: Now.AddDays(-7));

        Assert.IsTrue(decision.Send);
        Assert.AreEqual(MemoryAuditAlertReason.Repeat, decision.Reason);
    }

    [TestMethod]
    public void AZeroRepeatIntervalNeverRepeatsAnUnchangedWarning()
    {
        var decision = Decide(Snapshot(Malformed), last: [Malformed], lastAt: Now.AddDays(-400),
            options: new MemoryAuditOptions { AlertRepeatInterval = TimeSpan.Zero });

        Assert.IsFalse(decision.Send);
    }

    [TestMethod]
    public void ANewlyFailingInvariantIsSent()
    {
        var decision = Decide(Snapshot(Malformed, Growth), last: [Malformed], lastAt: Now.AddHours(-1));

        Assert.IsTrue(decision.Send);
        Assert.AreEqual(MemoryAuditAlertReason.Changed, decision.Reason);
        CollectionAssert.AreEqual(new[] { Growth }, decision.Added.ToArray());
        Assert.AreEqual(0, decision.Cleared.Count);
    }

    [TestMethod]
    public void AnInvariantClearingWhileAnotherStillFailsIsSent()
    {
        var decision = Decide(Snapshot(Malformed), last: [Growth, Malformed], lastAt: Now.AddHours(-1));

        Assert.IsTrue(decision.Send);
        Assert.AreEqual(MemoryAuditAlertReason.Changed, decision.Reason);
        CollectionAssert.AreEqual(new[] { Growth }, decision.Cleared.ToArray());
    }

    [TestMethod]
    public void AnAlertSeverityFindingIsSentOnEveryRunEvenWhenUnchanged()
    {
        var decision = Decide(Snapshot(HardDelete, Malformed), last: [HardDelete, Malformed], lastAt: Now.AddMinutes(-5));

        Assert.IsTrue(decision.Send);
        Assert.AreEqual(MemoryAuditAlertReason.Alert, decision.Reason);
    }

    [TestMethod]
    public void NothingIsSentWhenAlertingIsTurnedOff()
    {
        var decision = Decide(Snapshot(HardDelete), last: [], lastAt: null,
            options: new MemoryAuditOptions { AlertOnAttention = false });

        Assert.IsFalse(decision.Send);
    }

    [TestMethod]
    public void TheCurrentSetIsDistinctAndOrdinalSorted()
    {
        var decision = Decide(Snapshot(Malformed, Growth, Malformed), last: [], lastAt: null);

        CollectionAssert.AreEqual(new[] { Growth, Malformed }.Order(StringComparer.Ordinal).ToArray(),
            decision.Current.ToArray());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MemoryAuditAlertDecision Decide(
        MemoryAuditSnapshot snapshot,
        IReadOnlyList<string> last,
        DateTimeOffset? lastAt,
        MemoryAuditOptions? options = null) =>
        MemoryAuditAlertPolicy.Decide(snapshot, last, lastAt, options ?? new MemoryAuditOptions(), Now);

    private static MemoryAuditSnapshot Snapshot(params string[] failing)
    {
        List<MemoryAuditInvariantViolation> violations = [.. failing.Select(name => new MemoryAuditInvariantViolation(name, "msg", []))];
        return new MemoryAuditSnapshot
        {
            SnapshotId = "snap",
            TakenAt = Now,
            Invariants = violations,
            Status = MemoryAuditInvariants.ComputeStatus(violations)
        };
    }
}
