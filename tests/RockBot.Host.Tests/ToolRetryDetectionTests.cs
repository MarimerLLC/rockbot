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
}
