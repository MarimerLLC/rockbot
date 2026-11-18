using RockBot.Llm;

namespace RockBot.Host.Tests;

/// <summary>
/// Tier routing's second opinion: asked only for score-band decisions near a ceiling, confident
/// answers only, at most one tier away from the keyword verdict.
/// </summary>
[TestClass]
public class TierRoutingRefinerTests
{
    private static TierClassification Near(ModelTier tier, string rule = TierRoutingRules.ScoreBand, double distance = 0.03) =>
        new(tier, 0.52, [], []) { Rule = rule, BoundaryDistance = distance, IntrinsicTier = tier };

    [TestMethod]
    public async Task NearBoundary_ConfidentModel_MovesOneStep()
    {
        var decisions = new FakeDecisionService(choice: "high");

        var refined = await new TierRoutingRefiner(decisions)
            .RefineAsync("Work out why the nightly build is flaky", "user-message", Near(ModelTier.Balanced));

        Assert.AreEqual(ModelTier.High, refined.Tier);
        Assert.AreEqual(TierRoutingRules.DecisionModel, refined.Rule);
        Assert.AreEqual(ModelTier.High, refined.IntrinsicTier);
        Assert.AreEqual(DecisionPurposes.TierRouting, decisions.Requests.Single().Purpose);
        // #846: the bump is recorded for the routing log, keyword tier included.
        Assert.IsNotNull(refined.Refinement);
        Assert.AreEqual(ModelTier.Balanced, refined.Refinement.KeywordTier);
        Assert.AreEqual(ModelTier.High, refined.Refinement.ModelChoice);
        Assert.IsTrue(refined.Refinement.Applied);
    }

    [TestMethod]
    public async Task NeverMoreThanOneStep()
    {
        var refined = await new TierRoutingRefiner(new FakeDecisionService(choice: "high"))
            .RefineAsync("thanks, and research the whole topic", "user-message", Near(ModelTier.Low));

        Assert.AreEqual(ModelTier.Balanced, refined.Tier);
        Assert.AreEqual(ModelTier.High, refined.Refinement?.ModelChoice, "Log the model's answer, not the clamped tier.");
        Assert.IsTrue(refined.Refinement!.Applied);
    }

    [TestMethod]
    public async Task PromotionCeiling_HoldsAnUpwardMove_RecordsItAsCapped()
    {
        // #846: a scheduled task's subagent scored 0.52 (Balanced) and the model said High.
        var classification = Near(ModelTier.Balanced);

        var refined = await new TierRoutingRefiner(new FakeDecisionService(choice: "high"))
            .RefineAsync("sweep the inbox, then check the calendar", "subagent", classification,
                promotionCeiling: ModelTier.Balanced);

        Assert.AreEqual(ModelTier.Balanced, refined.Tier);
        Assert.AreEqual(TierRoutingRules.ScoreBand, refined.Rule);
        Assert.AreEqual(new TierRefinement(ModelTier.Balanced, ModelTier.High, 0.9, 0.6, Applied: false, Capped: true),
            refined.Refinement);
    }

    [TestMethod]
    public async Task PromotionCeiling_StillAllowsMovesBelowIt_AndNeverLowersAKeywordHigh()
    {
        var refiner = new TierRoutingRefiner(new FakeDecisionService(choice: "balanced"));
        var up = await refiner.RefineAsync("x", "subagent", Near(ModelTier.Low), promotionCeiling: ModelTier.Balanced);
        Assert.AreEqual(ModelTier.Balanced, up.Tier, "Low → Balanced is fine for a scheduled task's subagent");
        Assert.IsFalse(up.Refinement!.Capped);

        // Keyword already High: the ceiling limits promotions, not the keyword verdict. The model's
        // own downward opinion still applies as before.
        var high = Near(ModelTier.High, TierRoutingRules.HighGate);
        var stays = await new TierRoutingRefiner(new FakeDecisionService(choice: "high"))
            .RefineAsync("x", "subagent", high, promotionCeiling: ModelTier.Balanced);
        Assert.AreEqual(ModelTier.High, stays.Tier);
        Assert.IsFalse(stays.Refinement!.Capped);
    }

    [TestMethod]
    public async Task ModelAgrees_KeepsTierAndRule_RecordsTheAnswer()
    {
        var classification = Near(ModelTier.Balanced);

        var refined = await new TierRoutingRefiner(new FakeDecisionService(choice: "balanced"))
            .RefineAsync("check the calendar", "subagent", classification);

        Assert.AreEqual(ModelTier.Balanced, refined.Tier);
        Assert.AreEqual(TierRoutingRules.ScoreBand, refined.Rule);
        Assert.AreEqual(new TierRefinement(ModelTier.Balanced, ModelTier.Balanced, 0.9, 0.6, Applied: false),
            refined.Refinement);
    }

    [TestMethod]
    [DataRow(TierRoutingRules.ActiveThreadFloor, 0.03, DisplayName = "thread floor decided")]
    [DataRow(TierRoutingRules.TrivialGuard, 0.03, DisplayName = "trivial guard decided")]
    [DataRow(TierRoutingRules.InheritedTier, 0.03, DisplayName = "inherited")]
    [DataRow(TierRoutingRules.ScoreBand, 0.2, DisplayName = "far from a boundary")]
    public async Task DeterministicOrClearCases_NoCall(string rule, double distance)
    {
        var decisions = new FakeDecisionService(choice: "high");
        var classification = Near(ModelTier.Balanced, rule, distance);

        Assert.AreSame(classification,
            await new TierRoutingRefiner(decisions).RefineAsync("anything", "user-message", classification));
        Assert.AreEqual(0, decisions.Requests.Count);
    }

    [TestMethod]
    public async Task LowConfidence_KeepsTheKeywordTier_RecordsTheDeclinedAnswer()
    {
        var unsure = new FakeDecisionService(choice: "high");
        unsure.Thresholds[DecisionPurposes.TierRouting] = 0.95; // fake answers with confidence 0.9

        var classification = Near(ModelTier.Balanced);
        var refined = await new TierRoutingRefiner(unsure).RefineAsync("x", "user-message", classification);

        Assert.AreEqual(ModelTier.Balanced, refined.Tier);
        Assert.AreEqual(TierRoutingRules.ScoreBand, refined.Rule);
        Assert.AreEqual(new TierRefinement(ModelTier.Balanced, ModelTier.High, 0.9, 0.95, Applied: false),
            refined.Refinement);
    }

    [TestMethod]
    public async Task NoAnswerOrNoService_ReturnsTheClassificationUntouched()
    {
        var classification = Near(ModelTier.Balanced);
        Assert.AreSame(classification,
            await new TierRoutingRefiner(new FakeDecisionService(null)).RefineAsync("x", "user-message", classification));
        Assert.AreSame(classification, await new TierRoutingRefiner().RefineAsync("x", "user-message", classification));
    }
}
