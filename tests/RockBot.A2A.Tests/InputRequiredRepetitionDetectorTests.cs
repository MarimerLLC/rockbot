namespace RockBot.A2A.Tests;

[TestClass]
public class InputRequiredRepetitionDetectorTests
{
    [TestMethod]
    public void Track_ReturnsFalse_WhenBelowThreshold()
    {
        var detector = new InputRequiredRepetitionDetector(3);

        Assert.IsFalse(detector.Track("What time works?", "3pm works."));
        Assert.IsFalse(detector.Track("What time works?", "3pm works."));
    }

    [TestMethod]
    public void Track_ReturnsTrue_WhenThresholdReached()
    {
        var detector = new InputRequiredRepetitionDetector(3);

        Assert.IsFalse(detector.Track("What time works?", "3pm works."));
        Assert.IsFalse(detector.Track("What time works?", "3pm works."));
        Assert.IsTrue(detector.Track("What time works?", "3pm works."));
    }

    [TestMethod]
    public void Track_ResetsAfterThresholdReached()
    {
        var detector = new InputRequiredRepetitionDetector(3);

        // Trigger threshold
        detector.Track("Q", "A");
        detector.Track("Q", "A");
        Assert.IsTrue(detector.Track("Q", "A"));

        // After reset, same pair should start fresh
        Assert.IsFalse(detector.Track("Q", "A"));
    }

    [TestMethod]
    public void Track_ResetsCountOnDifferentPair()
    {
        var detector = new InputRequiredRepetitionDetector(3);

        detector.Track("Q1", "A1");
        detector.Track("Q1", "A1");
        // Different pair resets the count
        Assert.IsFalse(detector.Track("Q2", "A2"));
        Assert.IsFalse(detector.Track("Q2", "A2"));
        Assert.IsTrue(detector.Track("Q2", "A2"));
    }

    [TestMethod]
    public void Track_DifferentAnswer_IsNotRepetition()
    {
        var detector = new InputRequiredRepetitionDetector(3);

        Assert.IsFalse(detector.Track("What time?", "3pm"));
        Assert.IsFalse(detector.Track("What time?", "4pm"));
        Assert.IsFalse(detector.Track("What time?", "5pm"));
    }

    [TestMethod]
    public void Track_TruncatesLongStrings()
    {
        var detector = new InputRequiredRepetitionDetector(3);

        var longQuestion = new string('Q', 1000);
        var longAnswer = new string('A', 1000);

        // Should not throw and should still detect repetition
        Assert.IsFalse(detector.Track(longQuestion, longAnswer));
        Assert.IsFalse(detector.Track(longQuestion, longAnswer));
        Assert.IsTrue(detector.Track(longQuestion, longAnswer));
    }

    [TestMethod]
    public void Track_LongStrings_DifferOnlyAfterTruncation_AreConsideredSame()
    {
        var detector = new InputRequiredRepetitionDetector(3);

        var base500 = new string('X', 500);
        var q1 = base500 + "AAAA"; // 504 chars, truncated to 500
        var q2 = base500 + "BBBB"; // 504 chars, truncated to same 500

        Assert.IsFalse(detector.Track(q1, "A"));
        Assert.IsFalse(detector.Track(q2, "A"));
        Assert.IsTrue(detector.Track(q1, "A")); // Same truncated key
    }

    [TestMethod]
    public void Reset_ClearsState()
    {
        var detector = new InputRequiredRepetitionDetector(3);

        detector.Track("Q", "A");
        detector.Track("Q", "A");
        detector.Reset();

        // Should start fresh after explicit reset
        Assert.IsFalse(detector.Track("Q", "A"));
        Assert.IsFalse(detector.Track("Q", "A"));
    }

    [TestMethod]
    public void Track_ThresholdOfOne_TriggersImmediately()
    {
        var detector = new InputRequiredRepetitionDetector(1);

        Assert.IsTrue(detector.Track("Q", "A"));
    }
}
