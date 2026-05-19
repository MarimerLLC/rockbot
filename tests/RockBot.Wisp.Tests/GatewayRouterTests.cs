using System.Text.Json;
using RockBot.Wisp;

namespace RockBot.Wisp.Tests;

[TestClass]
public class GatewayRouterTests
{
    private static readonly Dictionary<string, WispStepResult> EmptyResults = new();

    // ── MCP gateway ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Route_Mcp_BuildsCorrectToolInvocation()
    {
        var step = new WispStep
        {
            Id = "download",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Mcp,
            Server = "onedrive",
            Tool = "download_file",
            Params = JsonDocument.Parse("""{"path": "/reports/Q1.xlsx"}""").RootElement
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("mcp_invoke_tool", result.ToolName);
        Assert.IsNotNull(result.Arguments);

        var args = JsonDocument.Parse(result.Arguments!).RootElement;
        Assert.AreEqual("onedrive", args.GetProperty("server_name").GetString());
        Assert.AreEqual("download_file", args.GetProperty("tool_name").GetString());
        Assert.AreEqual("/reports/Q1.xlsx", args.GetProperty("arguments").GetProperty("path").GetString());
    }

    [TestMethod]
    public void Route_Mcp_WithInputAlias_PassesArgumentsCorrectly()
    {
        var step = new WispStep
        {
            Id = "fetch_events",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Mcp,
            Server = "calendar-mcp",
            Tool = "get_calendar_events",
            // LLM used "input" instead of "params" — should still route correctly
            Input = JsonDocument.Parse("""{"timeZone": "America/Chicago", "startDate": "2025-04-01"}""").RootElement
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("mcp_invoke_tool", result.ToolName);
        Assert.IsNotNull(result.Arguments);

        var args = JsonDocument.Parse(result.Arguments!).RootElement;
        Assert.AreEqual("calendar-mcp", args.GetProperty("server_name").GetString());
        Assert.AreEqual("get_calendar_events", args.GetProperty("tool_name").GetString());
        Assert.AreEqual("America/Chicago",
            args.GetProperty("arguments").GetProperty("timeZone").GetString());
    }

    [TestMethod]
    public void Route_Mcp_WithArgsInInputFrom_RescuesAndRoutes()
    {
        // LLM put tool arguments in input_from as a JSON string
        var step = new WispStep
        {
            Id = "fetch_events",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Mcp,
            Server = "calendar-mcp",
            Tool = "get_calendar_events",
            InputFrom = """{"timeZone": "America/Chicago", "startDate": "2025-04-01"}"""
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual("mcp_invoke_tool", result.ToolName);

        var args = JsonDocument.Parse(result.Arguments!).RootElement;
        Assert.AreEqual("America/Chicago",
            args.GetProperty("arguments").GetProperty("timeZone").GetString());
    }

    [TestMethod]
    public void Route_Mcp_MissingParams_DefaultsToEmptyArguments()
    {
        // No-argument MCP tools (e.g. list_calendars) should route successfully
        // with an empty arguments object — the MCP server validates required args
        // against its own schema and returns a tool-specific error if needed.
        var step = new WispStep
        {
            Id = "list",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Mcp,
            Server = "ms365",
            Tool = "list_calendars"
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual("mcp_invoke_tool", result.ToolName);

        var args = JsonDocument.Parse(result.Arguments!).RootElement;
        Assert.AreEqual("ms365", args.GetProperty("server_name").GetString());
        Assert.AreEqual("list_calendars", args.GetProperty("tool_name").GetString());
        Assert.IsTrue(args.TryGetProperty("arguments", out var argsObj));
        Assert.AreEqual(JsonValueKind.Object, argsObj.ValueKind);
        Assert.AreEqual(0, argsObj.EnumerateObject().Count());
    }

    [TestMethod]
    public void Route_Mcp_MissingServer_ReturnsStructuralError()
    {
        var step = new WispStep
        {
            Id = "bad",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Mcp,
            Tool = "download_file"
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.ErrorCategory);
        StringAssert.Contains(result.ErrorMessage, "server");
    }

    [TestMethod]
    public void Route_Mcp_MissingTool_ReturnsStructuralError()
    {
        var step = new WispStep
        {
            Id = "bad",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Mcp,
            Server = "onedrive"
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.ErrorCategory);
        StringAssert.Contains(result.ErrorMessage, "tool");
    }

    // ── A2A gateway ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Route_A2A_BuildsCorrectToolInvocation()
    {
        var step = new WispStep
        {
            Id = "analyze",
            Mode = StepMode.Direct,
            Gateway = GatewayType.A2A,
            Agent = "market-analyst",
            Skill = "competitive-analysis",
            Message = "Analyze the data",
            TimeoutMinutes = 10
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("invoke_agent", result.ToolName);

        var args = JsonDocument.Parse(result.Arguments!).RootElement;
        Assert.AreEqual("market-analyst", args.GetProperty("agent_name").GetString());
        Assert.AreEqual("competitive-analysis", args.GetProperty("skill").GetString());
        Assert.AreEqual("Analyze the data", args.GetProperty("message").GetString());
        Assert.AreEqual(10, args.GetProperty("timeout_minutes").GetInt32());
    }

    [TestMethod]
    public void Route_A2A_ForwardsMetadata_WhenStepProvidesIt()
    {
        var step = new WispStep
        {
            Id = "filter-mentions",
            Mode = StepMode.Direct,
            Gateway = GatewayType.A2A,
            Agent = "SocialAgent",
            Skill = "recent-mentions",
            Message = "Bluesky only.",
            Metadata = JsonDocument.Parse("""{"providerId":"bluesky","count":10}""").RootElement
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsTrue(result.IsSuccess);
        var args = JsonDocument.Parse(result.Arguments!).RootElement;
        Assert.IsTrue(args.TryGetProperty("metadata", out var md));
        Assert.AreEqual("bluesky", md.GetProperty("providerId").GetString());
        Assert.AreEqual(10, md.GetProperty("count").GetInt32());
    }

    [TestMethod]
    public void Route_A2A_OmitsMetadata_WhenStepHasNone()
    {
        var step = new WispStep
        {
            Id = "no-md",
            Mode = StepMode.Direct,
            Gateway = GatewayType.A2A,
            Agent = "TargetAgent",
            Skill = "summarize",
            Message = "x"
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsTrue(result.IsSuccess);
        var args = JsonDocument.Parse(result.Arguments!).RootElement;
        Assert.IsFalse(args.TryGetProperty("metadata", out _),
            "metadata key must not appear when the wisp step doesn't supply it.");
    }

    [TestMethod]
    public void Route_A2A_MissingAgent_ReturnsStructuralError()
    {
        var step = new WispStep
        {
            Id = "bad",
            Mode = StepMode.Direct,
            Gateway = GatewayType.A2A,
            Skill = "test",
            Message = "test"
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.ErrorCategory);
    }

    // ── Script gateway ───────────────────────────────────────────────────────

    [TestMethod]
    public void Route_Script_BuildsCorrectToolInvocation()
    {
        var step = new WispStep
        {
            Id = "run",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Script,
            Language = "python",
            Params = JsonDocument.Parse("""{"script": "print('hello')", "timeout_seconds": 30}""").RootElement
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("execute_python_script", result.ToolName);
        Assert.IsNotNull(result.Arguments);

        var args = JsonDocument.Parse(result.Arguments!).RootElement;
        Assert.AreEqual("print('hello')", args.GetProperty("script").GetString());
    }

    [TestMethod]
    public void Route_Script_DefaultLanguage_UsesPython()
    {
        var step = new WispStep
        {
            Id = "run",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Script,
            Params = JsonDocument.Parse("""{"script": "print('hello')"}""").RootElement
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("execute_python_script", result.ToolName);
    }

    [TestMethod]
    public void Route_Script_MissingParams_ReturnsStructuralError()
    {
        var step = new WispStep
        {
            Id = "bad",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Script
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.ErrorCategory);
    }

    [TestMethod]
    public void Route_Script_MissingScriptInParams_ReturnsStructuralError()
    {
        var step = new WispStep
        {
            Id = "bad",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Script,
            Params = JsonDocument.Parse("""{"timeout_seconds": 30}""").RootElement
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.ErrorCategory);
    }

    // ── Web gateway ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Route_Web_Search_BuildsCorrectToolInvocation()
    {
        var step = new WispStep
        {
            Id = "search",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Web,
            Tool = "web_search",
            Params = JsonDocument.Parse("""{"query": "test query", "count": 5}""").RootElement
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("web_search", result.ToolName);

        var args = JsonDocument.Parse(result.Arguments!).RootElement;
        Assert.AreEqual("test query", args.GetProperty("query").GetString());
    }

    [TestMethod]
    public void Route_Web_Browse_BuildsCorrectToolInvocation()
    {
        var step = new WispStep
        {
            Id = "browse",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Web,
            Tool = "web_browse",
            Params = JsonDocument.Parse("""{"url": "https://example.com"}""").RootElement
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("web_browse", result.ToolName);
    }

    [TestMethod]
    public void Route_Web_InvalidTool_ReturnsStructuralError()
    {
        var step = new WispStep
        {
            Id = "bad",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Web,
            Tool = "web_crawl"
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.ErrorCategory);
    }

    [TestMethod]
    public void Route_Web_MissingTool_ReturnsStructuralError()
    {
        var step = new WispStep
        {
            Id = "bad",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Web
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.ErrorCategory);
    }

    // ── Null gateway ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Route_NullGateway_ReturnsStructuralError()
    {
        var step = new WispStep
        {
            Id = "bad",
            Mode = StepMode.Direct
        };

        var result = GatewayRouter.Route(step, "wisp-123", EmptyResults);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.ErrorCategory);
    }

    // ── Template resolution ──────────────────────────────────────────────────

    [TestMethod]
    public void ResolveTemplateString_ReplacesStepResult()
    {
        var priorResults = new Dictionary<string, WispStepResult>
        {
            ["search"] = new()
            {
                StepId = "search",
                StepIndex = 0,
                IsSuccess = true,
                Content = "search results here",
                Duration = TimeSpan.Zero
            }
        };

        var resolved = GatewayRouter.ResolveTemplateString(
            "Process this: {{steps.search.result}}", priorResults);

        Assert.AreEqual("Process this: search results here", resolved);
    }

    [TestMethod]
    public void ResolveTemplateString_NoMatch_ReturnsOriginal()
    {
        var resolved = GatewayRouter.ResolveTemplateString(
            "No templates here", EmptyResults);

        Assert.AreEqual("No templates here", resolved);
    }

    [TestMethod]
    public void ResolveTemplateString_NullContent_ReplacesWithEmpty()
    {
        var priorResults = new Dictionary<string, WispStepResult>
        {
            ["empty"] = new()
            {
                StepId = "empty",
                StepIndex = 0,
                IsSuccess = true,
                Content = null,
                Duration = TimeSpan.Zero
            }
        };

        var resolved = GatewayRouter.ResolveTemplateString(
            "Data: {{steps.empty.result}}", priorResults);

        Assert.AreEqual("Data: ", resolved);
    }

    [TestMethod]
    public void ResolveTemplateString_WithFieldPath_ExtractsJsonFieldValue()
    {
        var priorResults = new Dictionary<string, WispStepResult>
        {
            ["extract"] = new()
            {
                StepId = "extract",
                StepIndex = 0,
                IsSuccess = true,
                Content = """{"accountId":"xebia","calendarId":"abc","eventId":"evt-1"}""",
                Duration = TimeSpan.Zero
            }
        };

        var resolved = GatewayRouter.ResolveTemplateString(
            "{\"accountId\":\"{{steps.extract.result.accountId}}\",\"calendarId\":\"{{steps.extract.result.calendarId}}\"}",
            priorResults);

        Assert.AreEqual(
            "{\"accountId\":\"xebia\",\"calendarId\":\"abc\"}",
            resolved);
    }

    [TestMethod]
    public void ResolveTemplateString_WithNestedFieldPath_ExtractsDeepValue()
    {
        var priorResults = new Dictionary<string, WispStepResult>
        {
            ["find"] = new()
            {
                StepId = "find",
                StepIndex = 0,
                IsSuccess = true,
                Content = """{"event":{"id":"evt-42","meta":{"organizer":"bruce@example.com"}}}""",
                Duration = TimeSpan.Zero
            }
        };

        var resolved = GatewayRouter.ResolveTemplateString(
            "{{steps.find.result.event.meta.organizer}}", priorResults);

        Assert.AreEqual("bruce@example.com", resolved);
    }

    [TestMethod]
    public void ResolveTemplateString_FieldPathOnNonJsonContent_LeavesLiteral()
    {
        var priorResults = new Dictionary<string, WispStepResult>
        {
            ["text"] = new()
            {
                StepId = "text",
                StepIndex = 0,
                IsSuccess = true,
                Content = "not json at all",
                Duration = TimeSpan.Zero
            }
        };

        var resolved = GatewayRouter.ResolveTemplateString(
            "{{steps.text.result.someField}}", priorResults);

        Assert.AreEqual("{{steps.text.result.someField}}", resolved,
            "Unresolvable path should leave the literal in place so downstream validation can flag it");
    }

    [TestMethod]
    public void ResolveTemplateString_FieldPathMissing_LeavesLiteral()
    {
        var priorResults = new Dictionary<string, WispStepResult>
        {
            ["s"] = new()
            {
                StepId = "s",
                StepIndex = 0,
                IsSuccess = true,
                Content = """{"present":"yes"}""",
                Duration = TimeSpan.Zero
            }
        };

        var resolved = GatewayRouter.ResolveTemplateString(
            "{{steps.s.result.missing}}", priorResults);

        Assert.AreEqual("{{steps.s.result.missing}}", resolved);
    }

    [TestMethod]
    public void ResolveTemplateString_ResultWithoutPath_StillReplacesWholeContent()
    {
        // Backwards compatibility: bare {{steps.id.result}} still inserts the full content
        var priorResults = new Dictionary<string, WispStepResult>
        {
            ["raw"] = new()
            {
                StepId = "raw",
                StepIndex = 0,
                IsSuccess = true,
                Content = """{"some":"json"}""",
                Duration = TimeSpan.Zero
            }
        };

        var resolved = GatewayRouter.ResolveTemplateString(
            "body: {{steps.raw.result}}", priorResults);

        // JSON content gets escaped for embedding
        StringAssert.Contains(resolved, "some");
        StringAssert.Contains(resolved, "json");
    }

    [TestMethod]
    public void ResolveTemplateString_FieldPathYieldingNonString_InsertsJsonRepresentation()
    {
        var priorResults = new Dictionary<string, WispStepResult>
        {
            ["s"] = new()
            {
                StepId = "s",
                StepIndex = 0,
                IsSuccess = true,
                Content = """{"count":42,"items":["a","b"]}""",
                Duration = TimeSpan.Zero
            }
        };

        var num = GatewayRouter.ResolveTemplateString(
            "{{steps.s.result.count}}", priorResults);
        Assert.AreEqual("42", num);

        var arr = GatewayRouter.ResolveTemplateString(
            "{{steps.s.result.items}}", priorResults);
        // Non-string values serialize to their JSON representation (escaped for embedding)
        StringAssert.Contains(arr, "a");
        StringAssert.Contains(arr, "b");
    }

    [TestMethod]
    public void ResolveTemplateString_ContentWithSpecialChars_EscapesForJson()
    {
        var priorResults = new Dictionary<string, WispStepResult>
        {
            ["step1"] = new()
            {
                StepId = "step1",
                StepIndex = 0,
                IsSuccess = true,
                Content = "line1\nline2\twith\"quotes\"",
                Duration = TimeSpan.Zero
            }
        };

        var resolved = GatewayRouter.ResolveTemplateString(
            "{{steps.step1.result}}", priorResults);

        // The content should be JSON-escaped for embedding in a JSON string
        // System.Text.Json uses \\n for newlines and \\u0022 for quotes
        StringAssert.Contains(resolved, "\\n");
        Assert.IsFalse(resolved.Contains('\n'), "Raw newlines should be escaped");
        Assert.IsFalse(resolved.Contains('"'), "Raw quotes should be escaped");
    }

    // ── GetToolName ──────────────────────────────────────────────────────────

    [TestMethod]
    public void GetToolName_Mcp_ReturnsMcpInvokeTool()
    {
        var step = new WispStep { Id = "s", Mode = StepMode.Direct, Gateway = GatewayType.Mcp };
        Assert.AreEqual("mcp_invoke_tool", GatewayRouter.GetToolName(step));
    }

    [TestMethod]
    public void GetToolName_A2A_ReturnsInvokeAgent()
    {
        var step = new WispStep { Id = "s", Mode = StepMode.Direct, Gateway = GatewayType.A2A };
        Assert.AreEqual("invoke_agent", GatewayRouter.GetToolName(step));
    }

    [TestMethod]
    public void GetToolName_Script_IncludesLanguage()
    {
        var step = new WispStep { Id = "s", Mode = StepMode.Direct, Gateway = GatewayType.Script, Language = "python" };
        Assert.AreEqual("execute_python_script", GatewayRouter.GetToolName(step));
    }

    [TestMethod]
    public void GetToolName_Script_DefaultsPython()
    {
        var step = new WispStep { Id = "s", Mode = StepMode.Direct, Gateway = GatewayType.Script };
        Assert.AreEqual("execute_python_script", GatewayRouter.GetToolName(step));
    }

    [TestMethod]
    public void GetToolName_Web_ReturnsToolField()
    {
        var step = new WispStep { Id = "s", Mode = StepMode.Direct, Gateway = GatewayType.Web, Tool = "web_browse" };
        Assert.AreEqual("web_browse", GatewayRouter.GetToolName(step));
    }

    // ── {{steps.id.output_to}} template ──────────────────────────────────────

    [TestMethod]
    public void ResolveTemplateString_OutputTo_ReplacesFromDefinition()
    {
        var definition = new WispDefinition
        {
            Description = "test",
            Steps =
            [
                new WispStep
                {
                    Id = "download",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Mcp,
                    Server = "test",
                    Tool = "test",
                    OutputTo = "wisp-data/file.json"
                }
            ]
        };

        var priorResults = new Dictionary<string, WispStepResult>
        {
            ["download"] = new()
            {
                StepId = "download",
                StepIndex = 0,
                IsSuccess = true,
                Content = "data",
                Duration = TimeSpan.Zero
            }
        };

        var resolved = GatewayRouter.ResolveTemplateString(
            "Read from {{steps.download.output_to}}", priorResults, definition);

        Assert.AreEqual("Read from wisp-data/file.json", resolved);
    }

    [TestMethod]
    public void ResolveTemplateString_OutputTo_NoDefinition_LeavesUnresolved()
    {
        var priorResults = new Dictionary<string, WispStepResult>
        {
            ["s1"] = new()
            {
                StepId = "s1",
                StepIndex = 0,
                IsSuccess = true,
                Duration = TimeSpan.Zero
            }
        };

        var resolved = GatewayRouter.ResolveTemplateString(
            "{{steps.s1.output_to}}", priorResults, definition: null);

        Assert.AreEqual("{{steps.s1.output_to}}", resolved);
    }

    [TestMethod]
    public void ResolveTemplateString_BothResultAndOutputTo_ResolvesBoth()
    {
        var definition = new WispDefinition
        {
            Description = "test",
            Steps =
            [
                new WispStep
                {
                    Id = "step1",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    OutputTo = "output/results.json"
                }
            ]
        };

        var priorResults = new Dictionary<string, WispStepResult>
        {
            ["step1"] = new()
            {
                StepId = "step1",
                StepIndex = 0,
                IsSuccess = true,
                Content = "search results",
                Duration = TimeSpan.Zero
            }
        };

        var resolved = GatewayRouter.ResolveTemplateString(
            "File at {{steps.step1.output_to}} contains {{steps.step1.result}}",
            priorResults, definition);

        Assert.AreEqual("File at output/results.json contains search results", resolved);
    }
}
