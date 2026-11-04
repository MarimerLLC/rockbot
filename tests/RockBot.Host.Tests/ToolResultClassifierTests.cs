namespace RockBot.Host.Tests;

[TestClass]
public class ToolResultClassifierTests
{
    // ── IsError ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void StartsWithErrorPrefix_DetectedAsError() =>
        Assert.IsTrue(ToolResultClassifier.IsError(
            "Error: Graph API error: The resource could not be found."));

    [TestMethod]
    public void PlainContent_NotAnError() =>
        Assert.IsFalse(ToolResultClassifier.IsError(
            """{"server":"onedrive-personal","files":[]}"""));

    [TestMethod]
    public void ContentMentioningErrorWord_NotDetected() =>
        Assert.IsFalse(ToolResultClassifier.IsError(
            "The user's last login attempt produced an Error: condition that the docs explain."),
            "Only the literal 'Error: ' prefix counts — substring matches would mis-flag normal content.");

    [TestMethod]
    public void LowercasePrefix_NotDetected() =>
        Assert.IsFalse(ToolResultClassifier.IsError("error: lowercase"),
            "RegistryToolFunction emits 'Error: ' with capital E; matching is case-sensitive.");

    [TestMethod]
    public void ErrorWithoutColon_NotDetected() =>
        Assert.IsFalse(ToolResultClassifier.IsError("Error happened, see below"),
            "Without the colon-space, this is just narrative text.");

    [TestMethod]
    public void EmptyString_NotAnError() =>
        Assert.IsFalse(ToolResultClassifier.IsError(""));

    [TestMethod]
    public void DoubleErrorPrefix_StillDetected() =>
        Assert.IsTrue(ToolResultClassifier.IsError("Error: Error: nested"),
            "Defensive: if the executor's content already started with 'Error', the doubled prefix still counts.");

    // ── IsTimeout (#762) ─────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("Error: MCP server 'calendar-mcp' timed out after 300ms. This is a transient error — retry the same tool call to continue.")]
    [DataRow("Error: MCP tool 'list_events' timed out after 30s — the MCP bridge did not respond.")]
    [DataRow("Error: execute_python_script timed out after 30s. Either increase timeout_seconds, simplify the script, or consider an alternative approach.")]
    [DataRow("Error: Regex match timed out after 1.0s on a single entry. Try a more specific pattern.")]
    [DataRow("Error: The operation has Timed Out.")]
    public void ErrorEnvelopeTimeout_IsTimeout(string result) =>
        Assert.IsTrue(ToolResultClassifier.IsTimeout(result));

    [TestMethod]
    public void SuccessfulResultQuotingTimedOut_NotATimeout() =>
        Assert.IsFalse(ToolResultClassifier.IsTimeout(
            "Found 7 memories:\n- [techorama] Demo 3 prep: invariant-globalization recovery timed out " +
            "before producing a restore/build result."),
            "Content that quotes the phrase is a successful call (#762).");

    [TestMethod]
    public void NonTimeoutError_NotATimeout() =>
        Assert.IsFalse(ToolResultClassifier.IsTimeout("Error: the resource could not be found"));

    [TestMethod]
    public void EmptyString_NotATimeout() =>
        Assert.IsFalse(ToolResultClassifier.IsTimeout(""));
}
