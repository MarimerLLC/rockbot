using RockBot.Host;

namespace RockBot.Subagent.Tests;

/// <summary>
/// A subagent checks its own claims of work before its result is relayed (#686). On 2026-10-10 a
/// subagent reported "seven prep blocks scheduled and verified" when one create had succeeded.
/// Another reported "updated the checklist" with no write at all. Both were relayed unchecked.
/// </summary>
[TestClass]
public class SubagentSelfCheckTests
{
    [TestMethod]
    public void SelfCheck_RunsOnlyOnClaimsOfWork()
    {
        CollectionAssert.AreEquivalent(
            new[] { CompletionEvalTrigger.SideEffect, CompletionEvalTrigger.BareClaim, CompletionEvalTrigger.ClaimedChange },
            SubagentRunner.SelfCheckTriggers.ToArray());
    }

    [TestMethod]
    public void SelfCheck_CatchesBothIncidents()
    {
        const string task = "Schedule seven solo prep blocks and verify each one.";
        var batch = new LoopToolCall("spawn_wisps", null, false)
        {
            Detail = "6 of 7 wisps failed",
            Nested = [new LoopToolCall("calendar-mcp__create_event", null, true), new LoopToolCall("calendar-mcp__create_event", null, false)],
        };

        Assert.AreEqual(CompletionEvalTrigger.SideEffect, CompletionEvalTriggers.Decide(
            "Scheduled and verified seven solo prep blocks.", task, [batch], subagentSynthesis: false,
            enabled: SubagentRunner.SelfCheckTriggers));

        Assert.AreEqual(CompletionEvalTrigger.ClaimedChange, CompletionEvalTriggers.Decide(
            "I have also updated the demo-readiness checklist.", task, [new LoopToolCall("file_read", "path=drafts/x.md", true)],
            subagentSynthesis: false, enabled: SubagentRunner.SelfCheckTriggers));
    }

    [TestMethod]
    public void SelfCheck_SkipsAPlainResearchResult()
    {
        Assert.AreEqual(CompletionEvalTrigger.None, CompletionEvalTriggers.Decide(
            "The spec limits messages to 64 KiB (section 4.2).", "Find the message size limit in the spec.",
            [new LoopToolCall("web_search", "query=spec limit", true)], subagentSynthesis: false,
            enabled: SubagentRunner.SelfCheckTriggers));
    }
}
