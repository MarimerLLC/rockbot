namespace RockBot.Host.Tests;

[TestClass]
public class DreamServiceMergeHelpersTests
{
    [TestMethod]
    public void MergeSubjectTimeMetadata_EmptySources_ReturnsNull()
    {
        var result = DreamService.MergeSubjectTimeMetadata([]);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void MergeSubjectTimeMetadata_NoSubjectTimeKeys_ReturnsNull()
    {
        var sources = new[]
        {
            MakeEntry("a", metadata: new Dictionary<string, string> { ["importance"] = "0.8" }),
            MakeEntry("b", metadata: null)
        };

        var result = DreamService.MergeSubjectTimeMetadata(sources);

        Assert.IsNull(result, "Non-subject-time metadata keys should not propagate.");
    }

    [TestMethod]
    public void MergeSubjectTimeMetadata_PointFromOneSource_PreservedOnMerge()
    {
        var sources = new[]
        {
            MakeEntry("a", metadata: new Dictionary<string, string> { ["subjectTime"] = "2019-06" }),
            MakeEntry("b", metadata: null)
        };

        var result = DreamService.MergeSubjectTimeMetadata(sources);

        Assert.IsNotNull(result);
        Assert.AreEqual("2019-06", result["subjectTime"]);
    }

    [TestMethod]
    public void MergeSubjectTimeMetadata_ConflictingPoints_PrefersMoreSpecific()
    {
        var sources = new[]
        {
            MakeEntry("a", metadata: new Dictionary<string, string> { ["subjectTime"] = "2019" }),
            MakeEntry("b", metadata: new Dictionary<string, string> { ["subjectTime"] = "2019-06-14" })
        };

        var result = DreamService.MergeSubjectTimeMetadata(sources);

        Assert.IsNotNull(result);
        Assert.AreEqual("2019-06-14", result["subjectTime"],
            "When sources disagree on a subject-time point, the longer (more specific) value should win.");
    }

    [TestMethod]
    public void MergeSubjectTimeMetadata_RangeBounds_WidenAcrossSources()
    {
        var sources = new[]
        {
            MakeEntry("a", metadata: new Dictionary<string, string>
            {
                ["subjectTimeStart"] = "1998",
                ["subjectTimeEnd"] = "2001"
            }),
            MakeEntry("b", metadata: new Dictionary<string, string>
            {
                ["subjectTimeStart"] = "1995",
                ["subjectTimeEnd"] = "2003"
            })
        };

        var result = DreamService.MergeSubjectTimeMetadata(sources);

        Assert.IsNotNull(result);
        Assert.AreEqual("1995", result["subjectTimeStart"], "Start should be the earliest across sources.");
        Assert.AreEqual("2003", result["subjectTimeEnd"], "End should be the latest across sources.");
    }

    [TestMethod]
    public void MergeSubjectTimeMetadata_MixedPointAndRange_BothSurvive()
    {
        var sources = new[]
        {
            MakeEntry("a", metadata: new Dictionary<string, string> { ["subjectTime"] = "2019-06-14" }),
            MakeEntry("b", metadata: new Dictionary<string, string>
            {
                ["subjectTimeStart"] = "1995",
                ["subjectTimeEnd"] = "2003"
            })
        };

        var result = DreamService.MergeSubjectTimeMetadata(sources);

        Assert.IsNotNull(result);
        Assert.AreEqual("2019-06-14", result["subjectTime"]);
        Assert.AreEqual("1995", result["subjectTimeStart"]);
        Assert.AreEqual("2003", result["subjectTimeEnd"]);
    }

    [TestMethod]
    public void FormatSubjectTimeForPrompt_NullMetadata_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, DreamService.FormatSubjectTimeForPrompt(null));
    }

    [TestMethod]
    public void FormatSubjectTimeForPrompt_PointValue_Formats()
    {
        var meta = new Dictionary<string, string> { ["subjectTime"] = "2019-06" };
        Assert.AreEqual(" subject=2019-06", DreamService.FormatSubjectTimeForPrompt(meta));
    }

    [TestMethod]
    public void FormatSubjectTimeForPrompt_RangeValue_Formats()
    {
        var meta = new Dictionary<string, string>
        {
            ["subjectTimeStart"] = "1995",
            ["subjectTimeEnd"] = "2003"
        };
        Assert.AreEqual(" subject=1995..2003", DreamService.FormatSubjectTimeForPrompt(meta));
    }

    [TestMethod]
    public void FormatSubjectTimeForPrompt_OpenEndedRange_UsesQuestionMark()
    {
        var meta = new Dictionary<string, string> { ["subjectTimeStart"] = "2020" };
        Assert.AreEqual(" subject=2020..?", DreamService.FormatSubjectTimeForPrompt(meta));
    }

    private static MemoryEntry MakeEntry(string id, IReadOnlyDictionary<string, string>? metadata = null) =>
        new(id, "content", null, [], DateTimeOffset.UtcNow, Metadata: metadata);
}
