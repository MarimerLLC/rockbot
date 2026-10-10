using RockBot.Tools;

namespace RockBot.Tools.Tests;

/// <summary>
/// <c>convert_time</c> (#685): wall-clock conversion uses the DST rules of the date in question,
/// so the week when Europe has left summer time and the US hasn't comes out right.
/// </summary>
[TestClass]
public class TimeConversionTests
{
    [TestMethod]
    public void AmsterdamToChicago_InTheEuUsDstGap_Is0345()
    {
        // EU summer time ended Oct 25 2026; US ends Nov 1. CET (UTC+1) vs CDT (UTC-5): six hours.
        var result = TimeConversion.Convert("2026-10-28T09:45", "Europe/Amsterdam", "America/Chicago");

        StringAssert.Contains(result, "2026-10-28 09:45 Europe/Amsterdam (UTC+01:00)");
        StringAssert.Contains(result, "2026-10-28 03:45 America/Chicago (UTC-05:00)");
        StringAssert.Contains(result, "3:45 AM");
        StringAssert.Contains(result, "2026-10-28 08:45 UTC");
    }

    [TestMethod]
    public void AmsterdamToChicago_BeforeEitherChange_IsSevenHours()
    {
        var result = TimeConversion.Convert("2026-10-20 09:45", "Europe/Amsterdam", "America/Chicago");

        StringAssert.Contains(result, "(UTC+02:00)");
        StringAssert.Contains(result, "2026-10-20 02:45 America/Chicago (UTC-05:00)");
    }

    [TestMethod]
    public void ExplicitOffset_IsHonoured_WithoutFromZone()
    {
        var result = TimeConversion.Convert("2026-10-28T08:45:00Z", null, "America/Chicago");

        StringAssert.Contains(result, "2026-10-28 03:45 America/Chicago");
    }

    [TestMethod]
    public void NonexistentLocalTime_IsReported()
    {
        // 02:30 on the spring-forward day does not exist in Chicago.
        var result = TimeConversion.Convert("2026-03-08T02:30", "America/Chicago", "Europe/Amsterdam");

        StringAssert.StartsWith(result, "Error:");
        StringAssert.Contains(result, "does not exist");
    }

    [TestMethod]
    public void AmbiguousLocalTime_NotesBothOccurrences()
    {
        // 01:30 happens twice in Chicago when the clocks go back on Nov 1 2026.
        var result = TimeConversion.Convert("2026-11-01T01:30", "America/Chicago", "UTC");

        StringAssert.Contains(result, "occurs twice");
        StringAssert.Contains(result, "2026-11-01 06:30 UTC");
    }

    [TestMethod]
    public void UnknownZone_IsAnError()
    {
        var result = TimeConversion.Convert("2026-10-28T09:45", "Europe/Atlantis", "America/Chicago");

        StringAssert.StartsWith(result, "Error:");
        StringAssert.Contains(result, "Europe/Atlantis");
    }

    [TestMethod]
    public async Task Executor_ReadsJsonArguments()
    {
        var response = await new ConvertTimeExecutor().ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "c1",
            ToolName = "convert_time",
            Arguments = """{"datetime":"2026-10-28T09:45","from_timezone":"Europe/Amsterdam","to_timezone":"America/Chicago"}""",
        }, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "03:45 America/Chicago");
    }
}
