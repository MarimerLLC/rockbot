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
    }

    [TestMethod]
    public async Task NeverMoreThanOneStep()
    {
        var refined = await new TierRoutingRefiner(new FakeDecisionService(choice: "high"))
            .RefineAsync("thanks, and research the whole topic", "user-message", Near(ModelTier.Low));

        Assert.AreEqual(ModelTier.Balanced, refined.Tier);
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
    public async Task LowConfidenceOrFailure_KeepsTheKeywordTier()
    {
        var unsure = new FakeDecisionService(choice: "high");
        unsure.Thresholds[DecisionPurposes.TierRouting] = 0.95; // fake answers with confidence 0.9

        var classification = Near(ModelTier.Balanced);
        Assert.AreSame(classification, await new TierRoutingRefiner(unsure).RefineAsync("x", "user-message", classification));
        Assert.AreSame(classification,
            await new TierRoutingRefiner(new FakeDecisionService(null)).RefineAsync("x", "user-message", classification));
        Assert.AreSame(classification, await new TierRoutingRefiner().RefineAsync("x", "user-message", classification));
    }
}
