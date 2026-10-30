using System.Globalization;

namespace RockBot.Host.Tests;

/// <summary>
/// The per-turn datetime line built by <see cref="AgentLoopRunner.BuildDateTimeContext"/> (#740).
/// </summary>
[TestClass]
public sealed class DateTimeContextTests
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(72);

    // Friday Oct 30 2026, 10:03:20 UTC: Chicago is on CDT (-05:00), Amsterdam on CET (+01:00).
    private static readonly DateTimeOffset UtcNow = new(2026, 10, 30, 10, 3, 20, TimeSpan.Zero);

    private CultureInfo? _culture;

    [TestInitialize]
    public void UseInvariantCulture()
    {
        _culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    [TestCleanup]
    public void RestoreCulture() => CultureInfo.CurrentCulture = _culture!;

    private static UserLocation Fix(
        DateTimeOffset reportedAt, string? locality = "Amsterdam", string? cc = "NL") =>
        new(52.37403, 4.88969, 3, reportedAt, "Europe/Amsterdam", locality, cc);

    [TestMethod]
    public void HomeOnly_NoLocation_IsUnchangedFromBeforeLocation()
    {
        var clock = AgentClockTests.MakeClock();

        var text = AgentLoopRunner.BuildDateTimeContext(UtcNow, clock, null, TimeSpan.Zero);

        Assert.AreEqual(
            "The user's local date and time is: Friday, October 30, 2026 05:03 -05:00 (America/Chicago). " +
            "Always express dates and times to the user in this timezone. Never assume UTC or any other timezone. " +
            "When any tool returns a UTC timestamp, convert it to this local timezone before using or displaying it.",
            text);
    }

    [TestMethod]
    public void Away_UsesCurrentZone_AndNamesHomeZone()
    {
        var clock = AgentClockTests.MakeClock();
        clock.SetCurrentZone(AgentClockTests.Amsterdam);

        var text = AgentLoopRunner.BuildDateTimeContext(UtcNow, clock, null, MaxAge);

        StringAssert.StartsWith(text,
            "The user's local date and time is: Friday, October 30, 2026 11:03 +01:00 (Europe/Amsterdam).");
        StringAssert.Contains(text,
            "The user's home timezone is America/Chicago; scheduled tasks marked \"home\" run on home time.");
    }

    [TestMethod]
    public void AtHome_WithFix_DoesNotMentionHomeZone()
    {
        var clock = AgentClockTests.MakeClock();

        var text = AgentLoopRunner.BuildDateTimeContext(
            UtcNow, clock, Fix(UtcNow.AddMinutes(-5), "Chicago", "US"), MaxAge);

        Assert.IsFalse(text.Contains("home timezone"), text);
        StringAssert.Contains(text, "The user's last known location is Chicago, US (reported 04:58).");
    }

    [TestMethod]
    public void FreshFix_WithLocality_NamesCityAndLocalReportTime()
    {
        var clock = AgentClockTests.MakeClock();
        clock.SetCurrentZone(AgentClockTests.Amsterdam);

        var text = AgentLoopRunner.BuildDateTimeContext(
            UtcNow, clock, Fix(new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero)), MaxAge);

        StringAssert.EndsWith(text, " The user's last known location is Amsterdam, NL (reported 11:00).");
    }

    [TestMethod]
    public void FreshFix_WithoutLocality_FallsBackToRoundedCoordinates()
    {
        var clock = AgentClockTests.MakeClock();
        clock.SetCurrentZone(AgentClockTests.Amsterdam);

        var text = AgentLoopRunner.BuildDateTimeContext(
            UtcNow, clock, Fix(UtcNow.AddMinutes(-3), locality: null, cc: null), MaxAge);

        StringAssert.Contains(text, "The user's last known location is approximately 52.4, 4.9 (reported 11:00).");
        Assert.IsFalse(text.Contains("52.37"), "Precise coordinates must never reach the prompt");
        Assert.IsFalse(text.Contains("4.88"), "Precise coordinates must never reach the prompt");
    }

    [TestMethod]
    public void FixFromAnotherDay_ShowsTheDate()
    {
        var clock = AgentClockTests.MakeClock();
        clock.SetCurrentZone(AgentClockTests.Amsterdam);

        var text = AgentLoopRunner.BuildDateTimeContext(UtcNow, clock, Fix(UtcNow.AddDays(-1)), MaxAge);

        StringAssert.Contains(text, "(reported 11:03, Oct 29)");
    }

    [TestMethod]
    public void StaleFix_IsOmitted()
    {
        var clock = AgentClockTests.MakeClock();

        var text = AgentLoopRunner.BuildDateTimeContext(UtcNow, clock, Fix(UtcNow.AddHours(-73)), MaxAge);

        Assert.IsFalse(text.Contains("last known location"), text);
    }

    [TestMethod]
    public void SameFix_SameMinute_IsIdentical_AndLocationTailIsStableAcrossMinutes()
    {
        var clock = AgentClockTests.MakeClock();
        clock.SetCurrentZone(AgentClockTests.Amsterdam);
        var fix = Fix(UtcNow.AddMinutes(-10));

        var first = AgentLoopRunner.BuildDateTimeContext(UtcNow, clock, fix, MaxAge);
        var sameMinute = AgentLoopRunner.BuildDateTimeContext(UtcNow.AddSeconds(30), clock, fix, MaxAge);
        var nextMinute = AgentLoopRunner.BuildDateTimeContext(UtcNow.AddMinutes(1), clock, fix, MaxAge);

        Assert.AreEqual(first, sameMinute);

        const string end = "before using or displaying it.";
        Assert.AreEqual(
            first[(first.IndexOf(end, StringComparison.Ordinal) + end.Length)..],
            nextMinute[(nextMinute.IndexOf(end, StringComparison.Ordinal) + end.Length)..]);
    }
}
