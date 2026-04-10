namespace RockBot.Host.Tests;

[TestClass]
public class ToolCallFailureClassifierTests
{
    // ── Classify ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Classify_Thrashing_ReturnsThrashing()
    {
        var result = ToolCallFailureClassifier.Classify("some error", isThrashing: true);
        Assert.AreEqual(ToolCallFailureCategory.Thrashing, result);
    }

    [TestMethod]
    public void Classify_Thrashing_TakesPrecedenceOverOtherSignals()
    {
        // Even with an unknown tool signal, thrashing wins.
        var result = ToolCallFailureClassifier.Classify("timeout", isUnknownTool: true, isThrashing: true);
        Assert.AreEqual(ToolCallFailureCategory.Thrashing, result);
    }

    [TestMethod]
    public void Classify_UnknownTool_ReturnsStructural()
    {
        var result = ToolCallFailureClassifier.Classify("some error", isUnknownTool: true);
        Assert.AreEqual(ToolCallFailureCategory.Structural, result);
    }

    // ── External patterns ────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("Error: request timed out")]
    [DataRow("Operation timeout after 30s")]
    [DataRow("deadline exceeded for tool call")]
    public void Classify_Timeout_ReturnsExternal(string error)
    {
        var result = ToolCallFailureClassifier.Classify(error);
        Assert.AreEqual(ToolCallFailureCategory.External, result);
    }

    [TestMethod]
    [DataRow("Error: rate limit exceeded")]
    [DataRow("429 Too Many Requests")]
    [DataRow("Request was throttled")]
    public void Classify_RateLimit_ReturnsExternal(string error)
    {
        var result = ToolCallFailureClassifier.Classify(error);
        Assert.AreEqual(ToolCallFailureCategory.External, result);
    }

    [TestMethod]
    [DataRow("Error: service unavailable")]
    [DataRow("503 Service Unavailable")]
    [DataRow("connection refused")]
    [DataRow("ECONNREFUSED")]
    public void Classify_ServiceUnavailable_ReturnsExternal(string error)
    {
        var result = ToolCallFailureClassifier.Classify(error);
        Assert.AreEqual(ToolCallFailureCategory.External, result);
    }

    [TestMethod]
    [DataRow("authentication failed")]
    [DataRow("401 Unauthorized")]
    [DataRow("403 Forbidden")]
    [DataRow("authorization expired")]
    public void Classify_Auth_ReturnsExternal(string error)
    {
        var result = ToolCallFailureClassifier.Classify(error);
        Assert.AreEqual(ToolCallFailureCategory.External, result);
    }

    // ── Structural patterns ──────────────────────────────────────────────────

    [TestMethod]
    [DataRow("Error: unknown tool 'search_files{}'")]
    [DataRow("tool not found: my_tool")]
    public void Classify_UnknownToolMessage_ReturnsStructural(string error)
    {
        var result = ToolCallFailureClassifier.Classify(error);
        Assert.AreEqual(ToolCallFailureCategory.Structural, result);
    }

    [TestMethod]
    [DataRow("missing required param 'local_path'")]
    [DataRow("validation error: field 'path' is required")]
    [DataRow("pydantic validation error for DownloadFile")]
    [DataRow("unexpected keyword argument 'remote_path'")]
    public void Classify_InvalidParams_ReturnsStructural(string error)
    {
        var result = ToolCallFailureClassifier.Classify(error);
        Assert.AreEqual(ToolCallFailureCategory.Structural, result);
    }

    [TestMethod]
    [DataRow("invalid json in arguments")]
    [DataRow("parse error: unexpected token")]
    [DataRow("malformed request body")]
    public void Classify_MalformedInput_ReturnsStructural(string error)
    {
        var result = ToolCallFailureClassifier.Classify(error);
        Assert.AreEqual(ToolCallFailureCategory.Structural, result);
    }

    // ── Data patterns ────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("resource not found")]
    [DataRow("file not found: Apps/RockBot/teams.json")]
    [DataRow("404 Not Found")]
    [DataRow("path not found: /shared/data")]
    [DataRow("no such file or directory")]
    public void Classify_ResourceNotFound_ReturnsData(string error)
    {
        var result = ToolCallFailureClassifier.Classify(error);
        Assert.AreEqual(ToolCallFailureCategory.Data, result);
    }

    // ── Defaults ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Classify_UnrecognisedError_DefaultsToStructural()
    {
        var result = ToolCallFailureClassifier.Classify("something went wrong");
        Assert.AreEqual(ToolCallFailureCategory.Structural, result);
    }

    [TestMethod]
    public void Classify_NullError_DefaultsToStructural()
    {
        var result = ToolCallFailureClassifier.Classify(null);
        Assert.AreEqual(ToolCallFailureCategory.Structural, result);
    }

    [TestMethod]
    public void Classify_EmptyError_DefaultsToStructural()
    {
        var result = ToolCallFailureClassifier.Classify("");
        Assert.AreEqual(ToolCallFailureCategory.Structural, result);
    }

    // ── FindClosestToolName ──────────────────────────────────────────────────

    [TestMethod]
    public void FindClosest_ExactMatchAfterStripping_ReturnsMatch()
    {
        var tools = new[] { "search_files", "file_list", "file_read" };
        var result = ToolCallFailureClassifier.FindClosestToolName("search_files{}", tools);
        Assert.AreEqual("search_files", result);
    }

    [TestMethod]
    public void FindClosest_ExactMatchAfterStrippingBrackets_ReturnsMatch()
    {
        var tools = new[] { "search_files", "file_list", "file_read" };
        var result = ToolCallFailureClassifier.FindClosestToolName("search_files()", tools);
        Assert.AreEqual("search_files", result);
    }

    [TestMethod]
    public void FindClosest_CloseEditDistance_ReturnsMatch()
    {
        var tools = new[] { "search_files", "file_list", "file_read" };
        // "serch_files" is 1 edit away from "search_files"
        var result = ToolCallFailureClassifier.FindClosestToolName("serch_files", tools);
        Assert.AreEqual("search_files", result);
    }

    [TestMethod]
    public void FindClosest_TooFarAway_ReturnsNull()
    {
        var tools = new[] { "search_files", "file_list", "file_read" };
        var result = ToolCallFailureClassifier.FindClosestToolName("completely_different_tool", tools);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void FindClosest_EmptyName_ReturnsNull()
    {
        var tools = new[] { "search_files", "file_list" };
        var result = ToolCallFailureClassifier.FindClosestToolName("", tools);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void FindClosest_NoAvailableTools_ReturnsNull()
    {
        var result = ToolCallFailureClassifier.FindClosestToolName("search_files", []);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void FindClosest_CaseInsensitive_ReturnsMatch()
    {
        var tools = new[] { "Search_Files", "file_list" };
        var result = ToolCallFailureClassifier.FindClosestToolName("search_files", tools);
        Assert.AreEqual("Search_Files", result);
    }

    // ── LevenshteinDistance ───────────────────────────────────────────────────

    [TestMethod]
    public void LevenshteinDistance_IdenticalStrings_ReturnsZero()
    {
        Assert.AreEqual(0, ToolCallFailureClassifier.LevenshteinDistance("abc", "abc"));
    }

    [TestMethod]
    public void LevenshteinDistance_SingleInsertion_ReturnsOne()
    {
        Assert.AreEqual(1, ToolCallFailureClassifier.LevenshteinDistance("abc", "abcd"));
    }

    [TestMethod]
    public void LevenshteinDistance_SingleDeletion_ReturnsOne()
    {
        Assert.AreEqual(1, ToolCallFailureClassifier.LevenshteinDistance("abcd", "abc"));
    }

    [TestMethod]
    public void LevenshteinDistance_SingleSubstitution_ReturnsOne()
    {
        Assert.AreEqual(1, ToolCallFailureClassifier.LevenshteinDistance("abc", "axc"));
    }

    [TestMethod]
    public void LevenshteinDistance_EmptySource_ReturnsTargetLength()
    {
        Assert.AreEqual(3, ToolCallFailureClassifier.LevenshteinDistance("", "abc"));
    }

    [TestMethod]
    public void LevenshteinDistance_EmptyTarget_ReturnsSourceLength()
    {
        Assert.AreEqual(3, ToolCallFailureClassifier.LevenshteinDistance("abc", ""));
    }

    [TestMethod]
    public void LevenshteinDistance_BothEmpty_ReturnsZero()
    {
        Assert.AreEqual(0, ToolCallFailureClassifier.LevenshteinDistance("", ""));
    }
}
