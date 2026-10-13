using RockBot.Llm;

namespace RockBot.Host.Tests;

/// <summary>
/// Correction detection: deterministic first (no prior reply, acknowledgements, unambiguous
/// phrases, no cue at all), the decision model for weak cues, and the old regex when the model
/// can't answer.
/// </summary>
[TestClass]
public class CorrectionDetectorTests
{
    private const string AgentReply = "I scheduled the review for Tuesday at 3pm on your work calendar.";

    [TestMethod]
    [DataRow("that's wrong, it was Wednesday")]
    [DataRow("You were wrong about the venue")]
    [DataRow("that didn't work")]
    [DataRow("That's not what I asked for")]
    public void DefinitePhrases_AreCorrections(string message)
    {
        Assert.AreEqual(CorrectionDetector.Verdict.Yes, CorrectionDetector.Classify(message, AgentReply));
    }

    [TestMethod]
    [DataRow("thanks!")]
    [DataRow("Book a table for four at 7.")]
    [DataRow("What's the weather tomorrow?")]
    public void NoCueOrAcknowledgement_IsNotACorrection(string message)
    {
        Assert.AreEqual(CorrectionDetector.Verdict.No, CorrectionDetector.Classify(message, AgentReply));
    }

    [TestMethod]
    public void NoPreviousReply_IsNotACorrection()
    {
        Assert.AreEqual(CorrectionDetector.Verdict.No, CorrectionDetector.Classify("that's wrong", null));
    }

    [TestMethod]
    [DataRow("no problem, thanks for checking")]
    [DataRow("actually, can you also add Bob?")]
    [DataRow("I have no idea what time it starts")]
    [DataRow("it should be Wednesday")]
    public void WeakCues_AreAskedAbout(string message)
    {
        Assert.AreEqual(CorrectionDetector.Verdict.Ask, CorrectionDetector.Classify(message, AgentReply));
    }

    [TestMethod]
    public async Task WeakCue_ModelDecides()
    {
        var no = new FakeDecisionService(0.02);
        Assert.IsFalse(await new CorrectionDetector(no).IsCorrectionAsync("no problem, thanks for checking", AgentReply),
            "the legacy regex counted \"no problem\" as a correction");
        var request = no.Requests.Single();
        Assert.AreEqual(DecisionPurposes.Correction, request.Purpose);
        Assert.AreEqual(AgentReply, ((IDictionary<string, object?>)request.State)["agent_reply"]);

        Assert.IsTrue(await new CorrectionDetector(new FakeDecisionService(0.93))
            .IsCorrectionAsync("it should be Wednesday", AgentReply), "the legacy regex missed this one");
    }

    [TestMethod]
    public async Task ModelUnavailable_FallsBackToTheLegacyRegex()
    {
        var failing = new CorrectionDetector(new FakeDecisionService(null));

        Assert.IsTrue(await failing.IsCorrectionAsync("try again with the other one", AgentReply));
        Assert.IsFalse(await failing.IsCorrectionAsync("it should be Wednesday", AgentReply));
        Assert.IsTrue(await new CorrectionDetector().IsCorrectionAsync("actually the other one", AgentReply),
            "no model configured");
    }

    [TestMethod]
    public async Task DeterministicVerdicts_MakeNoCall()
    {
        var decisions = new FakeDecisionService(0.99);
        var detector = new CorrectionDetector(decisions);

        Assert.IsTrue(await detector.IsCorrectionAsync("that's wrong", AgentReply));
        Assert.IsFalse(await detector.IsCorrectionAsync("Book a table for four.", AgentReply));
        Assert.AreEqual(0, decisions.Requests.Count);
    }

    // ── Dream at-risk sessions read the evaluator's grade, not substrings ───

    [TestMethod]
    [DataRow("""{"summary":"ok","overallQuality":"good","toolsFailedOrMissed":["fairway_lookup"]}""", false,
        DisplayName = "tool name containing 'fair'")]
    [DataRow("""{"summary":"Handled an unfair billing dispute well","overallQuality":"good"}""", false,
        DisplayName = "'unfair' in the summary")]
    [DataRow("""{"summary":"Missed the deadline","overallQuality":"poor"}""", true)]
    [DataRow("""{"summary":"Okay","overallQuality":"Fair"}""", true)]
    public void LowQualitySummary_ReadsOverallQuality(string detail, bool expected)
    {
        var entry = new FeedbackEntry("id", "s", FeedbackSignalType.SessionSummary, "summary", detail, DateTimeOffset.UtcNow);

        Assert.AreEqual(expected, DreamService.IsLowQualitySessionSummary(entry));
    }

    [TestMethod]
    public void LowQualitySummary_WithoutJson_UsesWholeWordsInTheSummary()
    {
        Assert.IsTrue(DreamService.IsLowQualitySessionSummary(
            new FeedbackEntry("id", "s", FeedbackSignalType.SessionSummary, "Quality was poor.", "not json", DateTimeOffset.UtcNow)));
        Assert.IsFalse(DreamService.IsLowQualitySessionSummary(
            new FeedbackEntry("id", "s", FeedbackSignalType.SessionSummary, "Fairly smooth session.", null, DateTimeOffset.UtcNow)));
    }
}
