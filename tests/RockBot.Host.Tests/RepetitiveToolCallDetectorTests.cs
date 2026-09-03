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

    // ── Entropy normalization (issue #464) ───────────────────────────────────

    /// <summary>
    /// Builds a realistic spawn_wisps batch summary: a fresh batch/wisp ID and a
    /// different elapsed time on every call, but substantively the same outcome.
    /// </summary>
    private static string SpawnWispsResult(string batchId, string wispId, int ms, double totalSeconds) =>
        $"""
        1 wisp(s) completed (0 succeeded, 1 failed, {totalSeconds:F1}s total):

        - `{wispId}`: "check inbox" [failed] ({ms}ms)
          Error (ToolNotFound): Tool 'email_search' is not registered
          Tool: email_search

        Batch ID: `{batchId}`
        Batch summary: `wisp/batch-{batchId}/summary`
        """;

    [TestMethod]
    public void Track_SpawnWispsStyleResultWithGuids_StillTriggers()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        var batchIds = new[] { "batch-3f2a1b9c4d5e", "batch-a17c05e9b3d4", "batch-99f4c2a8e1b0" };
        var wispIds = new[] { "wisp-1a2b3c4d5e6f", "wisp-0f9e8d7c6b5a", "wisp-c4d3e2f1a0b9" };
        var durations = new[] { 812, 1043, 977 };
        var totals = new[] { 0.9, 1.1, 1.0 };

        bool triggered = false;
        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold; i++)
        {
            triggered = detector.Track(
                "spawn_wisps",
                "wisps=[check inbox]",
                SpawnWispsResult(batchIds[i], wispIds[i], durations[i], totals[i]));
        }

        Assert.IsTrue(triggered, "Per-call batch/wisp IDs and durations should not hide the repetition");
    }

    [TestMethod]
    public void Track_ResultDifferingOnlyByGuid_StillTriggers()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        var guids = new[]
        {
            "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
            "3fa85f64-5717-4562-b3fc-2c963f66afa6",
            "0e1d2c3b-4a59-4687-9f10-abcdef012345",
        };

        bool triggered = false;
        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold; i++)
            triggered = detector.Track("start_job", "name=triage", $"Job accepted. Correlation ID: {guids[i]}");

        Assert.IsTrue(triggered, "A varying GUID should not hide the repetition");
    }

    [TestMethod]
    public void Track_ResultDifferingOnlyByTimestamp_StillTriggers()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        var timestamps = new[]
        {
            "2026-06-05T14:03:22.123Z",
            "2026-06-05T14:03:25.907Z",
            "2026-06-05T14:03:31+02:00",
        };

        bool triggered = false;
        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold; i++)
            triggered = detector.Track("check_status", "id=42", $"[{timestamps[i]}] still pending");

        Assert.IsTrue(triggered, "A varying timestamp should not hide the repetition");
    }

    [TestMethod]
    public void Track_ResultDifferingOnlyByDuration_StillTriggers()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        var durations = new[] { "1234ms", "1.2s", "1.5 s" };

        bool triggered = false;
        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold; i++)
            triggered = detector.Track("run_query", "sql=select 1", $"Query timed out after {durations[i]}");

        Assert.IsTrue(triggered, "A varying duration should not hide the repetition");
    }

    [TestMethod]
    public void Track_EntropyBeyondTruncationBoundary_StillTriggers()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        // "{prefix} id=" is 498 chars, so the ID straddles the 500-char truncation
        // boundary: only two of its hex digits survive the cut. Normalizing after
        // truncation would leave that varying fragment in the key.
        var prefix = new string('x', 494);
        var ids = new[] { "1a2b3c4d5e6f7081", "90afbecd12345678", "fedcba9876543210" };

        bool triggered = false;
        for (var i = 0; i < AgentLoopRunner.RepetitiveToolCallDetector.Threshold; i++)
            triggered = detector.Track("big_tool", "a=1", $"{prefix} id={ids[i]}");

        Assert.IsTrue(triggered, "Normalization must run before truncation so IDs past the cut are neutralized");
    }

    [TestMethod]
    public void Track_SubstantivelyDifferentResult_StillResets()
    {
        var detector = new AgentLoopRunner.RepetitiveToolCallDetector();

        detector.Track("spawn_wisps", "wisps=[check inbox]", SpawnWispsResult("batch-3f2a1b9c4d5e", "wisp-1a2b3c4d5e6f", 812, 0.9));
        detector.Track("spawn_wisps", "wisps=[check inbox]", SpawnWispsResult("batch-a17c05e9b3d4", "wisp-0f9e8d7c6b5a", 1043, 1.1));

        // Same shape, but the wisp actually succeeded this time — a real difference.
        var triggered = detector.Track(
            "spawn_wisps",
            "wisps=[check inbox]",
            """
            1 wisp(s) completed (1 succeeded, 0 failed, 1.0s total):

            - `wisp-c4d3e2f1a0b9`: "check inbox" [ok] (977ms)
              Output: 3 unread messages

            Batch ID: `batch-99f4c2a8e1b0`
            Batch summary: `wisp/batch-batch-99f4c2a8e1b0/summary`
            """);

        Assert.IsFalse(triggered, "Normalization must not flatten genuinely different results");
    }

    [TestMethod]
    public void Threshold_IsThree()
    {
        Assert.AreEqual(3, AgentLoopRunner.RepetitiveToolCallDetector.Threshold);
    }
}
