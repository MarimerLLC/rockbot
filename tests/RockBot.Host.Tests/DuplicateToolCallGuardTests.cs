namespace RockBot.Host.Tests;

[TestClass]
public class DuplicateToolCallGuardTests
{
    [TestMethod]
    public void WasSeenBefore_FirstCall_ReturnsFalse()
    {
        var guard = new AgentLoopRunner.DuplicateToolCallGuard();

        var seen = guard.WasSeenBefore("get_message", "id=abc");

        Assert.IsFalse(seen, "The first call with a given (tool, args) pair must not be flagged as a duplicate");
    }

    [TestMethod]
    public void WasSeenBefore_SameToolAndArgs_ReturnsTrueOnRepeat()
    {
        var guard = new AgentLoopRunner.DuplicateToolCallGuard();

        guard.WasSeenBefore("get_message", "id=abc");
        var seenAgain = guard.WasSeenBefore("get_message", "id=abc");

        Assert.IsTrue(seenAgain, "An identical (tool, args) pair seen earlier in the run must be flagged");
    }

    [TestMethod]
    public void WasSeenBefore_NonConsecutiveRepeat_IsStillDetected()
    {
        var guard = new AgentLoopRunner.DuplicateToolCallGuard();

        guard.WasSeenBefore("get_message", "id=abc");
        guard.WasSeenBefore("get_attachment", "id=xyz");
        guard.WasSeenBefore("file_list", "prefix=attachments/");
        var seenAgain = guard.WasSeenBefore("get_message", "id=abc");

        Assert.IsTrue(seenAgain,
            "A repeat interleaved with other calls must be caught, unlike RepetitiveToolCallDetector's consecutive-only check");
    }

    [TestMethod]
    public void WasSeenBefore_DifferentArgs_IsNotADuplicate()
    {
        var guard = new AgentLoopRunner.DuplicateToolCallGuard();

        guard.WasSeenBefore("get_message", "id=abc");
        var seen = guard.WasSeenBefore("get_message", "id=xyz");

        Assert.IsFalse(seen, "Different arguments to the same tool must not be treated as a duplicate");
    }

    [TestMethod]
    public void WasSeenBefore_DifferentTool_SameArgs_IsNotADuplicate()
    {
        var guard = new AgentLoopRunner.DuplicateToolCallGuard();

        guard.WasSeenBefore("get_message", "id=abc");
        var seen = guard.WasSeenBefore("get_attachment", "id=abc");

        Assert.IsFalse(seen, "The same argument string on a different tool must not be treated as a duplicate");
    }

    [TestMethod]
    public void Reset_ClearsState()
    {
        var guard = new AgentLoopRunner.DuplicateToolCallGuard();

        guard.WasSeenBefore("get_message", "id=abc");
        guard.Reset();
        var seen = guard.WasSeenBefore("get_message", "id=abc");

        Assert.IsFalse(seen, "After Reset, a previously-seen call must be treated as new again");
    }
}
