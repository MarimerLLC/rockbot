namespace RockBot.Host.Tests;

/// <summary>
/// Stash keys and the per-run stash prefix (#532): the one place stash keys are built, and the
/// ambient prefix that lets <c>search_working_memory(namespace: "stash")</c> find them.
/// </summary>
[TestClass]
public class ToolResultStashTests
{
    [TestMethod]
    [DataRow(null, "stash/_")]
    [DataRow("", "stash/_")]
    [DataRow("sess-1", "stash/sess-1")]
    [DataRow("subagent-t1", "stash/subagent-t1")]
    public void KeyPrefix_UsesThePlaceholderForNoSession(string? sessionId, string expected) =>
        Assert.AreEqual(expected, ToolResultStash.KeyPrefix(sessionId));

    [TestMethod]
    public void Key_IsThePrefixPlusTheCallId()
    {
        Assert.AreEqual("stash/sess-1/call_1", ToolResultStash.Key("sess-1", "call_1"));
        Assert.AreEqual(ToolResultStash.Key("sess-1", "call_1"), AgentLoopRunner.BuildStashKey("sess-1", "call_1"),
            "The loop must build stash keys through ToolResultStash so the search prefix matches them.");
    }

    [TestMethod]
    public void CurrentPrefix_IsNullOutsideARun() =>
        Assert.IsNull(ToolResultStash.CurrentPrefix);

    [TestMethod]
    public void EnterRun_Nested_RestoresTheOuterPrefixOnDispose()
    {
        using (ToolResultStash.EnterRun("outer"))
        {
            Assert.AreEqual("stash/outer", ToolResultStash.CurrentPrefix);
            using (ToolResultStash.EnterRun("inner"))
                Assert.AreEqual("stash/inner", ToolResultStash.CurrentPrefix);
            Assert.AreEqual("stash/outer", ToolResultStash.CurrentPrefix);
        }
        Assert.IsNull(ToolResultStash.CurrentPrefix);
    }

    [TestMethod]
    public async Task EnterRun_FlowsIntoAwaitedWork()
    {
        using var _ = ToolResultStash.EnterRun("sess-1");

        var seen = await Task.Run(async () =>
        {
            await Task.Yield();
            return ToolResultStash.CurrentPrefix;
        });

        Assert.AreEqual("stash/sess-1", seen);
    }
}
