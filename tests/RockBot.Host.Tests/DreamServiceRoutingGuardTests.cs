using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

/// <summary>
/// Tests the deterministic ratchet-stop in the tier-routing review pass
/// (<see cref="DreamService.GuardBalancedCeilingDecrease"/>). The guard is the
/// "nothing trusts the LLM" backstop that prevents balancedCeiling from being driven
/// to its floor while High-tier routing is already over budget.
/// </summary>
[TestClass]
public class DreamServiceRoutingGuardTests
{
    [TestMethod]
    public void Guard_OverBudget_RejectsBalancedCeilingDecrease()
    {
        // High share 66.7% >> 20% target, LLM wants to LOWER the ceiling (more High) — reject.
        var result = DreamService.GuardBalancedCeilingDecrease(
            proposed: 0.40, current: 0.50, highPct: 66.7, targetPct: 20.0, NullLogger.Instance);
        Assert.AreEqual(0.50, result, "Must hold at current ceiling when over budget.");
    }

    [TestMethod]
    public void Guard_OverBudget_AllowsBalancedCeilingIncrease()
    {
        // Raising the ceiling moves traffic High→Balanced — that's the correction; allow it.
        var result = DreamService.GuardBalancedCeilingDecrease(
            proposed: 0.55, current: 0.50, highPct: 66.7, targetPct: 20.0, NullLogger.Instance);
        Assert.AreEqual(0.55, result);
    }

    [TestMethod]
    public void Guard_UnderBudget_AllowsDecrease()
    {
        // Within budget — the LLM's judgment is honored, including a decrease.
        var result = DreamService.GuardBalancedCeilingDecrease(
            proposed: 0.40, current: 0.50, highPct: 10.0, targetPct: 20.0, NullLogger.Instance);
        Assert.AreEqual(0.40, result);
    }

    [TestMethod]
    public void Guard_NullProposal_PassesThrough()
    {
        var result = DreamService.GuardBalancedCeilingDecrease(
            proposed: null, current: 0.50, highPct: 66.7, targetPct: 20.0, NullLogger.Instance);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Guard_NullFileCeiling_ComparesAgainstCompiledDefault()
    {
        // #844: the file had balancedCeiling null (selector on its compiled 0.55) and the LLM
        // "raised" it to 0.51. Against the effective ceiling that is a decrease, so over budget
        // it is rejected — and the file keeps null rather than pinning 0.55.
        var result = DreamService.GuardBalancedCeilingDecrease(
            proposed: 0.51, current: null, highPct: 66.7, targetPct: 20.0, NullLogger.Instance);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Guard_NullProposalOverTunedCeiling_IsADecrease()
    {
        // The proposal replaces the whole file, so null over a tuned 0.65 lowers the effective
        // ceiling to the compiled 0.55. Over budget, keep 0.65.
        var result = DreamService.GuardBalancedCeilingDecrease(
            proposed: null, current: 0.65, highPct: 30.0, targetPct: 20.0, NullLogger.Instance);
        Assert.AreEqual(0.65, result);
    }

    [TestMethod]
    public void EffectiveThresholdsBlock_NullConfig_ShowsCompiledDefaults()
    {
        var block = DreamService.EffectiveThresholdsBlock(new TierSelectorConfig());

        StringAssert.Contains(block, "lowCeiling = 0.25 (compiled default");
        StringAssert.Contains(block, "balancedCeiling = 0.55 (compiled default");
        StringAssert.Contains(block, "0.55 LOWERS it");
    }

    [TestMethod]
    public void EffectiveThresholdsBlock_FileValues_AreClampedAndLabelled()
    {
        var block = DreamService.EffectiveThresholdsBlock(
            new TierSelectorConfig { LowCeiling = 0.30, BalancedCeiling = 0.95 });

        StringAssert.Contains(block, "lowCeiling = 0.30 (from tier-selector.json");
        StringAssert.Contains(block, "balancedCeiling = 0.80 (from tier-selector.json");
    }
}
