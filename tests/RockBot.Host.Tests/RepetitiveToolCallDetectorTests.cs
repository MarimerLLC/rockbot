namespace RockBot.Host.Tests;

[TestClass]
public class RepetitiveToolCallDetectorTests
{
    // ── Track ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Track_BelowThreshold_ReturnsFalse()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold - 1; i++)
        {
            var triggered = detector.Track("file_delete", "path=teams/data.txt", "Error: File not found");
            Assert.IsFalse(triggered, $"Should not trigger on call {i + 1}");
        }
    }

    [TestMethod]
    public void Track_AtThreshold_ReturnsTrue()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        bool triggered = false;
        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold; i++)
        {
            triggered = detector.Track("file_delete", "path=teams/data.txt", "Error: File not found");
        }

        Assert.IsTrue(triggered, "Should trigger exactly at threshold");
    }

    [TestMethod]
    public void Track_AfterThreshold_ResetsAndRequiresAnotherThreshold()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        // Reach threshold
        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold; i++)
            detector.Track("file_delete", "path=data.txt", "Error: File not found");

        // State should be reset — another full threshold of calls is needed
        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold - 1; i++)
        {
            var triggered = detector.Track("file_delete", "path=data.txt", "Error: File not found");
            Assert.IsFalse(triggered, $"Should not trigger on post-reset call {i + 1}");
        }

        var finalTriggered = detector.Track("file_delete", "path=data.txt", "Error: File not found");
        Assert.IsTrue(finalTriggered, "Should trigger again after a full second run");
    }

    [TestMethod]
    public void Track_DifferentTool_ResetsCounter()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        // Two identical calls
        detector.Track("file_delete", "path=data.txt", "Error: File not found");
        detector.Track("file_delete", "path=data.txt", "Error: File not found");

        // Different tool call resets the run
        detector.Track("file_read", "path=data.txt", "some content");

        // Starting over — should not trigger at threshold - 1
        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold - 1; i++)
        {
            var triggered = detector.Track("file_delete", "path=data.txt", "Error: File not found");
            Assert.IsFalse(triggered);
        }
    }

    [TestMethod]
    public void Track_DifferentArgs_ResetsCounter()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        detector.Track("file_delete", "path=a.txt", "Error: File not found");
        detector.Track("file_delete", "path=a.txt", "Error: File not found");

        // Same tool, different args
        var triggered = detector.Track("file_delete", "path=b.txt", "Error: File not found");
        Assert.IsFalse(triggered, "Different args should reset the counter");
    }

    [TestMethod]
    public void Track_DifferentResult_ResetsCounter()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        detector.Track("my_tool", "x=1", "Error: not found");
        detector.Track("my_tool", "x=1", "Error: not found");

        // Same tool and args but different result
        var triggered = detector.Track("my_tool", "x=1", "Error: timeout");
        Assert.IsFalse(triggered, "Different result should reset the counter");
    }

    [TestMethod]
    public void Track_NonErrorResultCanAlsoTrigger()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        bool triggered = false;
        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold; i++)
            triggered = detector.Track("check_status", "id=42", "pending");

        Assert.IsTrue(triggered, "Should trigger even for non-error results when identical");
    }

    [TestMethod]
    public void Track_LongResultIsTruncatedForComparison()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        var longResult = new string('x', 600);

        bool triggered = false;
        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold; i++)
            triggered = detector.Track("my_tool", "a=1", longResult);

        Assert.IsTrue(triggered, "Long results should still trigger when identical");
    }

    [TestMethod]
    public void Reset_ClearsState()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        // Two identical calls
        detector.Track("file_delete", "path=x.txt", "Error: not found");
        detector.Track("file_delete", "path=x.txt", "Error: not found");

        detector.Reset();

        // After reset, threshold - 1 calls should not trigger
        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold - 1; i++)
        {
            var triggered = detector.Track("file_delete", "path=x.txt", "Error: not found");
            Assert.IsFalse(triggered);
        }
    }

    [TestMethod]
    public void Threshold_IsThree()
    {
        Assert.AreEqual(3, AgentLoopRunner.RepetitiveToolCallDetector.Threshold);
    }
}
