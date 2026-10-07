using System.Text.Json;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests;

/// <summary>
/// Pins the positive-evidence rule: a hint claims an argument problem only when the arguments
/// demonstrably contradict the schema, or the downstream narrowly says it rejected a value.
/// Cases follow mcp-aggregator#51 and its live test against adjutant's <c>move_email</c>.
/// </summary>
[TestClass]
public class McpCallDiagnosticsTests
{
    private const string MoveEmailSchema = """
        {
          "type": "object",
          "properties": {
            "emailId": { "type": "string" },
            "destination": { "type": "string" },
            "accountId": { "type": ["string", "null"] },
            "limit": { "type": "integer" },
            "score": { "type": "number" },
            "flags": { "type": "array", "items": { "type": "string" } },
            "options": { "type": "object", "properties": { "depth": { "type": "integer" } } },
            "target": { "anyOf": [ { "type": "string" }, { "type": "integer" } ] }
          },
          "required": ["emailId", "destination"]
        }
        """;

    private static Dictionary<string, object?> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);

    private static string? Describe(string argsJson, string? errorText, string? schema = MoveEmailSchema) =>
        McpCallDiagnostics.DescribeArgumentProblem("adjutant", "move_email", schema, Args(argsJson), errorText);

    // ── Errors on schema-valid arguments pass through unchanged ──────────────

    [TestMethod]
    public void RuntimeErrorWithDetail_OnValidArguments_GetsNoHint()
    {
        // The aggregator#50 trigger: a Graph bug surfaced through the SDK's prefix.
        var hint = Describe("""{"emailId":"AAMk","destination":"archive"}""",
            "An error occurred invoking 'move_email': ErrorInvalidIdMalformed: Id is malformed.");

        Assert.IsNull(hint);
    }

    [TestMethod]
    public void BareValidationWord_OnValidArguments_GetsNoHint()
    {
        var hint = Describe("""{"emailId":"AAMk","destination":"archive"}""",
            "Mailbox rule validation failed on the server.");

        Assert.IsNull(hint);
    }

    // ── Positive evidence: keys ──────────────────────────────────────────────

    [TestMethod]
    public void WrongKey_NamesMissingAndUnrecognisedKeys_WithSchema()
    {
        var hint = Describe("""{"emailId":"AAMk","folder":"archive"}""", "Something failed.");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "Missing required parameter(s): [destination]");
        StringAssert.Contains(hint, "Unrecognized argument key(s): [folder]");
        StringAssert.Contains(hint, "You sent: [emailId, folder]");
        StringAssert.Contains(hint, "\"destination\"");
    }

    [TestMethod]
    public void MissingFieldTheDownstreamAlreadyNamed_IsLeftToRecovery()
    {
        // Recovery fills environment defaults or enriches this case itself.
        var hint = Describe("""{"emailId":"AAMk"}""", "'destination' is required");

        Assert.IsNull(hint);
    }

    [TestMethod]
    public void MissingField_WithVagueError_IsReported()
    {
        var hint = Describe("""{"emailId":"AAMk"}""", "An error occurred invoking 'move_email'.");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "Missing required parameter(s): [destination]");
    }

    [TestMethod]
    public void KeySentAsNull_CountsAsPresent()
    {
        var hint = Describe("""{"emailId":"AAMk","destination":null}""", "Something failed.");

        Assert.IsNull(hint);
    }

    [TestMethod]
    public void SchemaWithoutProperties_NeverReportsUnrecognisedKeys()
    {
        var hint = Describe("""{"anything":1}""", "Something failed.", schema: """{"type":"object"}""");

        Assert.IsNull(hint);
    }

    // ── Positive evidence: top-level types ───────────────────────────────────

    [TestMethod]
    public void IntegerForString_IsAnExactTypeClaim()
    {
        var hint = Describe("""{"emailId":12345,"destination":"archive"}""", "Something failed.");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "Parameter 'emailId' is declared as string but you sent an integer.");
    }

    [TestMethod]
    public void StringForArray_IsATypeClaim()
    {
        var hint = Describe("""{"emailId":"a","destination":"b","flags":"urgent"}""", "Something failed.");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "Parameter 'flags' is declared as array but you sent a string.");
    }

    [TestMethod]
    public void UnionType_NamesEveryAlternative()
    {
        var hint = Describe("""{"emailId":"a","destination":"b","accountId":7}""", "Something failed.");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "Parameter 'accountId' is declared as string or null but you sent an integer.");
    }

    [TestMethod]
    [DataRow("""{"emailId":"a","destination":"b","accountId":"work"}""", DisplayName = "string satisfies [string, null]")]
    [DataRow("""{"emailId":"a","destination":"b","score":3}""", DisplayName = "integer satisfies number")]
    [DataRow("""{"emailId":"a","destination":"b","limit":3.0}""", DisplayName = "whole double satisfies integer")]
    [DataRow("""{"emailId":"a","destination":"b","target":true}""", DisplayName = "anyOf is skipped")]
    [DataRow("""{"emailId":"a","destination":"b","options":{"depth":"deep"}}""", DisplayName = "nested values are skipped")]
    [DataRow("""{"emailId":"a","destination":"b","limit":null}""", DisplayName = "null is skipped")]
    public void NoContradiction_GetsNoTypeClaim(string argsJson)
    {
        var hint = Describe(argsJson, "Something failed.");

        Assert.IsNull(hint);
    }

    [TestMethod]
    public void FractionForInteger_IsATypeClaim()
    {
        var hint = Describe("""{"emailId":"a","destination":"b","limit":2.5}""", "Something failed.");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "Parameter 'limit' is declared as integer but you sent a number.");
    }

    // ── Narrow signals from other SDKs ───────────────────────────────────────

    [TestMethod]
    [DataRow("MCP error -32602: Invalid arguments for tool move_email: expected string")]
    [DataRow("1 validation error for MoveEmailArgs\ndestination\n  String should have at least 1 character")]
    public void OtherSdkValidationText_GetsSchemaWithoutTypeClaim(string errorText)
    {
        var hint = Describe("""{"emailId":"a","destination":""}""", errorText);

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "The downstream rejected a value");
        StringAssert.Contains(hint, "Input schema:");
        Assert.IsFalse(hint.Contains("declared as", StringComparison.Ordinal));
    }

    // ── The C# SDK's detail-free failure ─────────────────────────────────────

    [TestMethod]
    public void BareSdkMessage_GetsHedgedNote()
    {
        var hint = Describe("""{"emailId":"a","destination":"b"}""", "An error occurred invoking 'move_email'.");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "gave no detail");
        StringAssert.Contains(hint, "otherwise the failure is on the downstream's side");
    }

    [TestMethod]
    public void BareSdkMessage_WithoutSchema_StillHedges()
    {
        var hint = Describe("""{"x":1}""", "An error occurred invoking 'move_email'.", schema: null);

        Assert.IsNotNull(hint);
        Assert.IsFalse(hint.Contains("Input schema", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NoSchema_AndOrdinaryError_GetsNoHint()
    {
        Assert.IsNull(Describe("""{"x":1}""", "Something failed.", schema: null));
    }

    [TestMethod]
    public void HintText_DoesNotLookLikeAMissingFieldErrorToRecovery()
    {
        // Recovery pattern-matches error text; a hint must not invent a "missing field" for it.
        var hint = Describe("""{"emailId":"a","folder":"b"}""", "Something failed.");

        Assert.IsNotNull(hint);
        Assert.IsFalse(SchemaErrorPatterns.TryExtractMissingField(hint, out var field), $"matched '{field}'");
    }

    // ── Unknown names ────────────────────────────────────────────────────────

    [TestMethod]
    public void UnknownTool_ListsTheRealNames()
    {
        var message = McpCallDiagnostics.DescribeUnknownTool("csla", "no_such_tool", ["fetch", "search", "version"]);

        StringAssert.Contains(message, "Unknown tool 'no_such_tool' on server 'csla'");
        StringAssert.Contains(message, "Available tools: [fetch, search, version]");
    }

    [TestMethod]
    public void CombinedServerAndToolName_IsSplitForTheModel()
    {
        var message = McpCallDiagnostics.DescribeUnknownTool("adjutant", "adjutant__send_email", ["send_email", "list_accounts"]);

        StringAssert.Contains(message, "server_name: \"adjutant\"");
        StringAssert.Contains(message, "tool_name: \"send_email\"");
    }

    [TestMethod]
    public void UnknownTool_OnServerWithNoTools_SaysSo()
    {
        var message = McpCallDiagnostics.DescribeUnknownTool("empty", "anything", []);

        StringAssert.Contains(message, "exposes no tools");
    }

    [TestMethod]
    public void UnknownServer_ListsRegisteredServersSorted()
    {
        var message = McpCallDiagnostics.DescribeUnknownServer("calendar", ["todo-mcp", "adjutant"]);

        StringAssert.Contains(message, "Unknown MCP server 'calendar'");
        StringAssert.Contains(message, "Registered servers: [adjutant, todo-mcp]");
        StringAssert.Contains(message, "renamed or removed");
    }

    [TestMethod]
    public void UnknownServer_WithNoneRegistered_SaysSo()
    {
        StringAssert.Contains(McpCallDiagnostics.DescribeUnknownServer("x", []), "no MCP servers are registered");
    }

    // ── Prompts ──────────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<McpPromptArgument> BriefingArgs =
    [
        new() { Name = "date", Description = "Day to brief on", Required = true },
        new() { Name = "accountId", Required = false },
    ];

    [TestMethod]
    public void PromptMissingRequiredArgument_ListsTheSignature()
    {
        var message = McpCallDiagnostics.DescribeMissingPromptArguments("adjutant", "daily_briefing", BriefingArgs, ["accountId"]);

        Assert.IsNotNull(message);
        StringAssert.Contains(message, "missing required argument(s): [date]");
        StringAssert.Contains(message, "date (required): Day to brief on");
        StringAssert.Contains(message, "accountId (optional)");
    }

    [TestMethod]
    public void PromptWithRequiredArgumentsPresent_PassesPreCheck()
    {
        Assert.IsNull(McpCallDiagnostics.DescribeMissingPromptArguments("adjutant", "daily_briefing", BriefingArgs, ["date"]));
    }

    [TestMethod]
    public void UnknownPrompt_ListsTheRealNames()
    {
        var message = McpCallDiagnostics.DescribeUnknownPrompt("adjutant", "briefing", ["daily_briefing", "email_triage"]);

        StringAssert.Contains(message, "Available prompts: [daily_briefing, email_triage]");
    }
}
