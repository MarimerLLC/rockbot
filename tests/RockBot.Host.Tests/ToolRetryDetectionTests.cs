namespace RockBot.Host.Tests;

[TestClass]
public class ToolRetryDetectionTests
{
    private static ToolCallEvent Evt(
        string session, string tool, string args, bool succeeded, int minutesAgo) =>
        new(
            SessionId: session,
            ToolName: tool,
            ArgumentsSummary: args,
            Succeeded: succeeded,
            DurationMs: 100,
            Timestamp: DateTimeOffset.UtcNow.AddMinutes(-minutesAgo));

    [TestMethod]
    public void NoEvents_ReturnsEmpty()
    {
        var patterns = DreamService.DetectToolRetryPatternsFromEvents([]);
        Assert.AreEqual(0, patterns.Count);
    }

    [TestMethod]
    public void SingleEvent_ReturnsEmpty()
    {
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            Evt("s1", "list_files", """{"folder":"X"}""", succeeded: true, minutesAgo: 5)
        ]);
        Assert.AreEqual(0, patterns.Count);
    }

    [TestMethod]
    public void SameArgsRepeated_NotARetryPattern()
    {
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            Evt("s1", "list_files", """{"folder":"X"}""", succeeded: false, minutesAgo: 10),
            Evt("s1", "list_files", """{"folder":"X"}""", succeeded: true,  minutesAgo: 5)
        ]);
        Assert.AreEqual(0, patterns.Count,
            "Same args with no variation isn't an ambiguity-resolution; it's a transient failure.");
    }

    [TestMethod]
    public void AllFailed_ReturnsEmpty()
    {
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            Evt("s1", "list_files", """{"server":"a"}""", succeeded: false, minutesAgo: 10),
            Evt("s1", "list_files", """{"server":"b"}""", succeeded: false, minutesAgo: 5)
        ]);
        Assert.AreEqual(0, patterns.Count, "No success means no verified value to learn from.");
    }

    [TestMethod]
    public void SuccessThenFailure_NotAPattern()
    {
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            Evt("s1", "list_files", """{"server":"a"}""", succeeded: true,  minutesAgo: 10),
            Evt("s1", "list_files", """{"server":"b"}""", succeeded: false, minutesAgo: 5)
        ]);
        Assert.AreEqual(0, patterns.Count,
            "Failure must precede success for this to indicate skill ambiguity.");
    }

    [TestMethod]
    public void FailureThenSuccess_DifferentArgs_ProducesPattern()
    {
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            Evt("s1", "list_files", """{"server":"onedrive-marimer"}""",  succeeded: false, minutesAgo: 10),
            Evt("s1", "list_files", """{"server":"onedrive-personal"}""", succeeded: true,  minutesAgo: 5)
        ]);

        Assert.AreEqual(1, patterns.Count);
        var p = patterns[0];
        Assert.AreEqual("s1", p.SessionId);
        Assert.AreEqual("list_files", p.ToolName);
        CollectionAssert.AreEqual(
            new[] { """{"server":"onedrive-marimer"}""" },
            p.FailedArgs.ToArray());
        Assert.AreEqual("""{"server":"onedrive-personal"}""", p.SuccessArgs);
    }

    [TestMethod]
    public void MultipleFailuresBeforeSuccess_AllCapturedUpToThree()
    {
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            Evt("s1", "list_files", """{"folder":"a"}""", succeeded: false, minutesAgo: 30),
            Evt("s1", "list_files", """{"folder":"b"}""", succeeded: false, minutesAgo: 25),
            Evt("s1", "list_files", """{"folder":"c"}""", succeeded: false, minutesAgo: 20),
            Evt("s1", "list_files", """{"folder":"d"}""", succeeded: false, minutesAgo: 15),
            Evt("s1", "list_files", """{"folder":"e"}""", succeeded: true,  minutesAgo: 10)
        ]);

        Assert.AreEqual(1, patterns.Count);
        Assert.AreEqual(3, patterns[0].FailedArgs.Count, "Failed-args list should cap at 3 to bound prompt size.");
        Assert.AreEqual("""{"folder":"e"}""", patterns[0].SuccessArgs);
    }

    [TestMethod]
    public void DistinctSessions_DoNotCrossContaminate()
    {
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            Evt("session-A", "list_files", """{"server":"a"}""", succeeded: false, minutesAgo: 10),
            Evt("session-B", "list_files", """{"server":"b"}""", succeeded: true,  minutesAgo: 5)
        ]);

        Assert.AreEqual(0, patterns.Count,
            "A failure in session A cannot be 'resolved' by a success in session B.");
    }

    [TestMethod]
    public void DifferentToolsInSameSession_TrackedSeparately()
    {
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            Evt("s1", "tool-X", """{"a":1}""", succeeded: false, minutesAgo: 10),
            Evt("s1", "tool-X", """{"a":2}""", succeeded: true,  minutesAgo: 5),
            Evt("s1", "tool-Y", """{"b":1}""", succeeded: false, minutesAgo: 8),
            Evt("s1", "tool-Y", """{"b":2}""", succeeded: true,  minutesAgo: 3)
        ]);

        Assert.AreEqual(2, patterns.Count);
        Assert.IsTrue(patterns.Any(p => p.ToolName == "tool-X"));
        Assert.IsTrue(patterns.Any(p => p.ToolName == "tool-Y"));
    }

    [TestMethod]
    public void SessionsFilter_ExcludesEventsOutsideTheSet()
    {
        var events = new[]
        {
            Evt("session-A", "list_files", """{"x":1}""", succeeded: false, minutesAgo: 10),
            Evt("session-A", "list_files", """{"x":2}""", succeeded: true,  minutesAgo: 5),
            Evt("session-B", "list_files", """{"y":1}""", succeeded: false, minutesAgo: 10),
            Evt("session-B", "list_files", """{"y":2}""", succeeded: true,  minutesAgo: 5)
        };

        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
            events,
            sessionsFilter: ["session-A"]);

        Assert.AreEqual(1, patterns.Count);
        Assert.AreEqual("session-A", patterns[0].SessionId);
    }

    [TestMethod]
    public void NullArgumentsSummary_NormalizedAndCounted()
    {
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            new ToolCallEvent("s1", "list_files", null, Succeeded: false, 100, DateTimeOffset.UtcNow.AddMinutes(-10)),
            Evt("s1", "list_files", """{"folder":"X"}""", succeeded: true, minutesAgo: 5)
        ]);

        Assert.AreEqual(1, patterns.Count);
        Assert.IsTrue(patterns[0].FailedArgs.Any(a => a.Contains("none")),
            "Null ArgumentsSummary should normalize to a placeholder.");
    }

    // ── Time-window behaviour (regression: long-running sessions) ────────────────

    [TestMethod]
    public void LongRunningSession_DetectsLaterFailureSuccessTransition()
    {
        // This is the exact bug we hit in the live cluster: blazor-session is one rolling
        // sessionId for weeks, and its first event is a success. The old "first success +
        // any earlier failure" logic missed every subsequent failure→success retry within
        // that bucket. The window-based logic must catch them.
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            // March 30: an unrelated successful call kicks off the session.
            new ToolCallEvent("blazor-session", "mcp_invoke_tool",
                """server_name=onedrive-marimer, query=LLM""",
                Succeeded: true, 100, DateTimeOffset.UtcNow.AddDays(-28)),

            // Today: agent guesses the wrong server, then the right one within minutes.
            new ToolCallEvent("blazor-session", "mcp_invoke_tool",
                """server_name=onedrive-marimer, folder=Apps/RockBot/xebia-teams""",
                Succeeded: false, 800, DateTimeOffset.UtcNow.AddMinutes(-15)),
            new ToolCallEvent("blazor-session", "mcp_invoke_tool",
                """server_name=onedrive-personal, folder=Apps/RockBot/xebia-teams""",
                Succeeded: true, 600, DateTimeOffset.UtcNow.AddMinutes(-5))
        ]);

        Assert.AreEqual(1, patterns.Count, "Should find the recent failure→success transition.");
        StringAssert.Contains(patterns[0].SuccessArgs, "onedrive-personal");
        Assert.AreEqual(1, patterns[0].FailedArgs.Count);
        StringAssert.Contains(patterns[0].FailedArgs[0], "onedrive-marimer");
    }

    [TestMethod]
    public void FailureOutsideLookbackWindow_NotPaired()
    {
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            // Failure 5 hours before success — far outside the default 30-minute window.
            new ToolCallEvent("s1", "list_files",
                """{"server":"a"}""",
                Succeeded: false, 100, DateTimeOffset.UtcNow.AddHours(-5)),
            new ToolCallEvent("s1", "list_files",
                """{"server":"b"}""",
                Succeeded: true, 100, DateTimeOffset.UtcNow.AddMinutes(-5))
        ]);

        Assert.AreEqual(0, patterns.Count,
            "A long-ago unrelated failure must not be paired with today's success.");
    }

    [TestMethod]
    public void FailureExactlyAtWindowEdge_Included()
    {
        var success = DateTimeOffset.UtcNow;
        var atEdge = success - DreamService.DefaultRetryLookbackWindow; // exactly the boundary

        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            new ToolCallEvent("s1", "list_files", "args-fail", Succeeded: false, 100, atEdge),
            new ToolCallEvent("s1", "list_files", "args-ok",   Succeeded: true,  100, success)
        ]);

        Assert.AreEqual(1, patterns.Count,
            "Failure timestamp == windowStart should still count (>=, not strict >).");
    }

    [TestMethod]
    public void CustomLookbackWindow_OverridesDefault()
    {
        // 35 minutes apart — outside default 30-min window, inside custom 60-min window.
        var events = new[]
        {
            new ToolCallEvent("s1", "list_files", "args-fail",
                Succeeded: false, 100, DateTimeOffset.UtcNow.AddMinutes(-40)),
            new ToolCallEvent("s1", "list_files", "args-ok",
                Succeeded: true,  100, DateTimeOffset.UtcNow.AddMinutes(-5))
        };

        var defaultPatterns = DreamService.DetectToolRetryPatternsFromEvents(events);
        var customPatterns = DreamService.DetectToolRetryPatternsFromEvents(
            events, lookbackWindow: TimeSpan.FromMinutes(60));

        Assert.AreEqual(0, defaultPatterns.Count, "Outside default 30-min window.");
        Assert.AreEqual(1, customPatterns.Count, "Inside custom 60-min window.");
    }

    [TestMethod]
    public void RepeatedSuccessSameArgsInBucket_MergedToOnePatternWithUnionedFailedArgs()
    {
        // Same lesson learned three times across the day: each time a different failed
        // server precedes the same successful server. One pattern, merged failed-args.
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            // First retry sequence
            new ToolCallEvent("s1", "mcp_invoke_tool",
                """server_name=A""", Succeeded: false, 100, DateTimeOffset.UtcNow.AddMinutes(-100)),
            new ToolCallEvent("s1", "mcp_invoke_tool",
                """server_name=Z""", Succeeded: true,  100, DateTimeOffset.UtcNow.AddMinutes(-95)),
            // Second
            new ToolCallEvent("s1", "mcp_invoke_tool",
                """server_name=B""", Succeeded: false, 100, DateTimeOffset.UtcNow.AddMinutes(-50)),
            new ToolCallEvent("s1", "mcp_invoke_tool",
                """server_name=Z""", Succeeded: true,  100, DateTimeOffset.UtcNow.AddMinutes(-45)),
            // Third
            new ToolCallEvent("s1", "mcp_invoke_tool",
                """server_name=C""", Succeeded: false, 100, DateTimeOffset.UtcNow.AddMinutes(-15)),
            new ToolCallEvent("s1", "mcp_invoke_tool",
                """server_name=Z""", Succeeded: true,  100, DateTimeOffset.UtcNow.AddMinutes(-10))
        ]);

        Assert.AreEqual(1, patterns.Count,
            "Same successArgs across multiple in-bucket retries collapses to one pattern.");
        Assert.AreEqual("""server_name=Z""", patterns[0].SuccessArgs);
        Assert.AreEqual(3, patterns[0].FailedArgs.Count,
            "Failed-args from each retry sequence should merge (capped at 3).");
        CollectionAssert.AreEquivalent(
            new[] { "server_name=A", "server_name=B", "server_name=C" },
            patterns[0].FailedArgs.ToArray());
    }

    [TestMethod]
    public void DifferentSuccessArgsInSameBucket_EachEmitsItsOwnPattern()
    {
        // Two distinct lessons learned in the same bucket should both surface.
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            new ToolCallEvent("s1", "mcp_invoke_tool",
                """server=A""", Succeeded: false, 100, DateTimeOffset.UtcNow.AddMinutes(-50)),
            new ToolCallEvent("s1", "mcp_invoke_tool",
                """server=Z""", Succeeded: true,  100, DateTimeOffset.UtcNow.AddMinutes(-45)),
            new ToolCallEvent("s1", "mcp_invoke_tool",
                """server=B""", Succeeded: false, 100, DateTimeOffset.UtcNow.AddMinutes(-15)),
            new ToolCallEvent("s1", "mcp_invoke_tool",
                """server=Y""", Succeeded: true,  100, DateTimeOffset.UtcNow.AddMinutes(-10))
        ]);

        Assert.AreEqual(2, patterns.Count);
        Assert.IsTrue(patterns.Any(p => p.SuccessArgs == "server=Z"));
        Assert.IsTrue(patterns.Any(p => p.SuccessArgs == "server=Y"));
    }

    [TestMethod]
    public void SuccessFollowedBySuccessSameArgs_NoSpuriousPattern()
    {
        // Once we've seen the lesson, repeated successes with the same args without any
        // intervening different-args failure should not re-emit the pattern.
        var patterns = DreamService.DetectToolRetryPatternsFromEvents(
        [
            new ToolCallEvent("s1", "list_files", "wrong",
                Succeeded: false, 100, DateTimeOffset.UtcNow.AddMinutes(-15)),
            new ToolCallEvent("s1", "list_files", "right",
                Succeeded: true,  100, DateTimeOffset.UtcNow.AddMinutes(-12)),
            new ToolCallEvent("s1", "list_files", "right",
                Succeeded: true,  100, DateTimeOffset.UtcNow.AddMinutes(-10)),
            new ToolCallEvent("s1", "list_files", "right",
                Succeeded: true,  100, DateTimeOffset.UtcNow.AddMinutes(-5))
        ]);

        Assert.AreEqual(1, patterns.Count);
        Assert.AreEqual(1, patterns[0].FailedArgs.Count,
            "The single 'wrong' failure should appear once even though 'right' succeeded thrice.");
    }
}
