using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests.Recovery;

[TestClass]
public class SchemaErrorPatternsTests
{
    [TestMethod]
    [DataRow("Required parameter 'timeZone'", "timeZone")]
    [DataRow("Required parameter \"accountId\"", "accountId")]
    [DataRow("Required parameter foo_bar", "foo_bar")]
    [DataRow("timeZone is required", "timeZone")]
    [DataRow("'startDate' is required to call this tool", "startDate")]
    [DataRow("missing required argument userId", "userId")]
    [DataRow("missing required argument 'token'", "token")]
    [DataRow("expected field name", "name")]
    [DataRow("expected field 'recipientEmail'", "recipientEmail")]
    [DataRow("frobnitz: must be provided", "frobnitz")]
    public void Match_ExtractsFieldName(string error, string expected)
    {
        var ok = SchemaErrorPatterns.TryExtractMissingField(error, out var field);
        Assert.IsTrue(ok, $"pattern should match: {error}");
        Assert.AreEqual(expected, field);
    }

    [TestMethod]
    [DataRow("connection refused")]
    [DataRow("HTTP 500: server error")]
    [DataRow("the calendar is empty")]
    [DataRow("")]
    [DataRow(null)]
    public void NoMatch_ReturnsFalse(string? error)
    {
        var ok = SchemaErrorPatterns.TryExtractMissingField(error, out var field);
        Assert.IsFalse(ok);
        Assert.AreEqual(string.Empty, field);
    }

    [TestMethod]
    public void Match_IsCaseInsensitive()
    {
        var ok = SchemaErrorPatterns.TryExtractMissingField("REQUIRED PARAMETER 'X'", out var field);
        Assert.IsTrue(ok);
        Assert.AreEqual("X", field);
    }

    [TestMethod]
    public void Match_GeneralizesToNovelFieldNames()
    {
        // The acceptance bullet from issue #345: a synthetic tool with a deliberately-novel
        // required field gets matched the same as known fields.
        var ok = SchemaErrorPatterns.TryExtractMissingField(
            "Required parameter 'quux_42'", out var field);
        Assert.IsTrue(ok);
        Assert.AreEqual("quux_42", field);
    }
}
