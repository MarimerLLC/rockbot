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
    public void Guard_NoCurrentConfig_PassesProposalThrough()
    {
        // No prior config to compare against — cannot be a "decrease"; honor the proposal.
        var result = DreamService.GuardBalancedCeilingDecrease(
            proposed: 0.40, current: null, highPct: 66.7, targetPct: 20.0, NullLogger.Instance);
        Assert.AreEqual(0.40, result);
    }
}
