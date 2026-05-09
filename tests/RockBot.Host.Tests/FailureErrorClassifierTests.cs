namespace RockBot.Host.Tests;

[TestClass]
public class FailureErrorClassifierTests
{
    [TestMethod]
    [DataRow("Required parameter 'timeZone'", "timeZone")]
    [DataRow("Required parameter \"accountId\"", "accountId")]
    [DataRow("Required parameter startDate", "startDate")]
    [DataRow("'timeZone' is required", "timeZone")]
    [DataRow("accountId is required", "accountId")]
    [DataRow("missing required argument 'endDate'", "endDate")]
    [DataRow("missing required argument tz", "tz")]
    [DataRow("expected field 'orgId'", "orgId")]
    [DataRow("expected field projectId", "projectId")]
    [DataRow("'timeZone': must be provided", "timeZone")]
    [DataRow("siteId: must be provided", "siteId")]
    public void Classify_ExtractsMissingFieldName(string error, string expected)
    {
        Assert.AreEqual(expected, FailureErrorClassifier.Classify(error));
    }

    [TestMethod]
    [DataRow("internal server error 500")]
    [DataRow("network timeout")]
    [DataRow("unauthorized")]
    [DataRow("")]
    [DataRow(null)]
    public void Classify_FallsBackToUnknown(string? error)
    {
        Assert.AreEqual(FailureErrorClassifier.Unknown, FailureErrorClassifier.Classify(error));
    }

    [TestMethod]
    public void Classify_IsCaseInsensitiveLikePhase1Patterns()
    {
        Assert.AreEqual("X", FailureErrorClassifier.Classify("REQUIRED PARAMETER 'X'"));
    }
}
