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
}
