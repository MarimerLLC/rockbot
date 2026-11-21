namespace RockBot.Host.Tests;

/// <summary>
/// #848: the deterministic half of a scheduled task's gate: empty detection, Changed mode, the
/// forced run after too many skips, and the date placeholders.
/// </summary>
[TestClass]
public sealed class ScheduledTaskGateTests
{
    private static readonly TaskGate Gate = new("mail", "new.json");

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("""{"messages":[]}""")]
    [DataRow("""{"value":[],"@odata.context":"x"}""")]
    [DataRow("""{"results":null}""")]
    [DataRow("""{"resultSizeEstimate":0,"nextPageToken":null}""")]
    [DataRow("""{"count":0}""")]
    [DataRow("""{"content":[{"type":"text","text":"[]"}]}""")]
    [DataRow("""{"content":[{"type":"text","text":"No messages found."}]}""")]
    [DataRow("No results found.")]
    [DataRow("0 new emails")]
    [DataRow("\"no matches\"")]
    public void IsEmpty_True(string? output) => Assert.IsTrue(ScheduledTaskGate.IsEmpty(output), output);

    [TestMethod]
    [DataRow("""[{"id":"m1"}]""")]
    [DataRow("""{"messages":[{"id":"m1"}]}""")]
    [DataRow("""{"count":2}""")]
    [DataRow("""{"total":5,"messages":[]}""")]
    [DataRow("""{"id":"m1","subject":"Tickets"}""")]
    [DataRow("""{"content":[{"type":"text","text":"[{\"id\":\"m1\"}]"}]}""")]
    [DataRow("Found 2 new emails from Ticketmaster.")]
    [DataRow("No new mail today, but one calendar invite arrived from the venue about your Saturday show, which may need a reply before the end of the week, and the promoter also wrote.")]
    [DataRow("{not json")]
    public void IsEmpty_False(string output) => Assert.IsFalse(ScheduledTaskGate.IsEmpty(output), output);

    [TestMethod]
    public void Evaluate_Results_Pass_AndResetSkips()
    {
        var decision = ScheduledTaskGate.Evaluate(Gate with { ConsecutiveSkips = 3 }, """[{"id":"m1"}]""");

        Assert.IsTrue(decision.RunLlm);
        Assert.AreEqual(ScheduledRunGateResult.Passed, decision.Result);
        Assert.AreEqual(0, decision.ConsecutiveSkips);
        Assert.AreEqual(ScheduledTaskGate.OutputHash("""[{"id":"m1"}]"""), decision.LastResultHash);
    }

    [TestMethod]
    public void Evaluate_Empty_Skips_AndKeepsTheLastHash()
    {
        var decision = ScheduledTaskGate.Evaluate(Gate with { LastResultHash = "prev", ConsecutiveSkips = 1 }, "[]");

        Assert.IsFalse(decision.RunLlm);
        Assert.AreEqual(ScheduledRunGateResult.Skipped, decision.Result);
        Assert.AreEqual(2, decision.ConsecutiveSkips);
        Assert.AreEqual("prev", decision.LastResultHash);
    }

    [TestMethod]
    public void Evaluate_ChangedMode_SameResultsSkip_NewResultsPass()
    {
        const string output = """[{"id":"m1"}]""";
        var gate = Gate with { Mode = TaskGateMode.Changed, LastResultHash = ScheduledTaskGate.OutputHash(output) };

        var same = ScheduledTaskGate.Evaluate(gate, "[{\"id\":\"m1\"}]\n");
        var changed = ScheduledTaskGate.Evaluate(gate, """[{"id":"m1"},{"id":"m2"}]""");
        var nonEmptyMode = ScheduledTaskGate.Evaluate(gate with { Mode = TaskGateMode.NonEmpty }, output);

        Assert.IsFalse(same.RunLlm, "whitespace is folded");
        Assert.IsTrue(changed.RunLlm);
        Assert.IsTrue(nonEmptyMode.RunLlm, "NonEmpty mode runs on any results");
    }

    [TestMethod]
    public void Evaluate_ChangedMode_FirstRun_Passes()
    {
        Assert.IsTrue(ScheduledTaskGate.Evaluate(Gate with { Mode = TaskGateMode.Changed }, """[{"id":"m1"}]""").RunLlm);
    }

    [TestMethod]
    public void Evaluate_AfterMaxSkips_ForcesARun()
    {
        var atLimit = ScheduledTaskGate.Evaluate(Gate with { MaxConsecutiveSkips = 2, ConsecutiveSkips = 1 }, "[]");
        var overLimit = ScheduledTaskGate.Evaluate(Gate with { MaxConsecutiveSkips = 2, ConsecutiveSkips = 2 }, "[]");
        var never = ScheduledTaskGate.Evaluate(Gate with { MaxConsecutiveSkips = 0, ConsecutiveSkips = 50 }, "[]");

        Assert.IsFalse(atLimit.RunLlm);
        Assert.AreEqual(2, atLimit.ConsecutiveSkips);
        Assert.IsTrue(overLimit.RunLlm);
        Assert.AreEqual(ScheduledRunGateResult.ForcedRun, overLimit.Result);
        Assert.AreEqual(0, overLimit.ConsecutiveSkips);
        Assert.IsFalse(never.RunLlm, "zero never forces a run");
    }

    [TestMethod]
    public void FillPlaceholders_UsesTheLastFire_InUtc()
    {
        var body = """{"q":"after:{{last_run_date}}","since":"{{LAST_RUN_AT}}"}""";
        var lastFired = new DateTimeOffset(2026, 11, 9, 23, 30, 0, TimeSpan.FromHours(-6));

        var filled = ScheduledTaskGate.FillPlaceholders(body, lastFired, DateTimeOffset.UtcNow);

        Assert.AreEqual("""{"q":"after:2026/11/10","since":"2026-11-10T05:30:00Z"}""", filled);
    }

    [TestMethod]
    public void FillPlaceholders_NeverFired_LooksBackThirtyDays_AndLeavesOtherTemplates()
    {
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

        var filled = ScheduledTaskGate.FillPlaceholders("{{last_run_date}} {{steps.a.result}}", null, now);

        Assert.AreEqual("2026/10/22 {{steps.a.result}}", filled);
        Assert.AreEqual("plain", ScheduledTaskGate.FillPlaceholders("plain", null, now));
    }
}
