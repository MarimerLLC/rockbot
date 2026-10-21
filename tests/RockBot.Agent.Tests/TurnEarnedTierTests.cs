using RockBot.Agent;
using RockBot.Host;

namespace RockBot.Agent.Tests;

/// <summary>
/// Covers the tier a finished user turn records in the session tier history (#729): its
/// mid-turn escalation, or Balanced when it did work on the user's behalf.
/// </summary>
[TestClass]
public class TurnEarnedTierTests
{
    private static LoopDiagnostics WithCalls(params string[] names)
    {
        var ledger = new LoopToolCallLedger();
        foreach (var name in names)
            ledger.Record(name, null, succeeded: true);
        return new LoopDiagnostics { ToolCallLedger = ledger };
    }

    [TestMethod]
    public void NoToolsNoEscalation_EarnsNothing()
    {
        Assert.IsNull(UserMessageHandler.EarnedTier(new LoopDiagnostics()));
    }

    [TestMethod]
    public void ContextReadsAndBookkeepingOnly_EarnNothing()
    {
        // The opening memory reads every turn makes, plus a memory save: small talk.
        var diagnostics = WithCalls("search_memory", "search_working_memory", "get_from_working_memory", "save_memory");

        Assert.IsNull(UserMessageHandler.EarnedTier(diagnostics));
    }

    [TestMethod]
    public void ExternalCall_EarnsBalanced()
    {
        // A Balanced-routed turn never escalates mid-turn, so this is what keeps a busy thread Balanced.
        var diagnostics = WithCalls("search_memory", "mcp_invoke_tool");

        Assert.AreEqual(ModelTier.Balanced, UserMessageHandler.EarnedTier(diagnostics));
    }

    [TestMethod]
    public void Escalation_IsKept()
    {
        var diagnostics = WithCalls("file_write");
        diagnostics.EscalatedTier = ModelTier.High;

        Assert.AreEqual(ModelTier.High, UserMessageHandler.EarnedTier(diagnostics));
    }
}
