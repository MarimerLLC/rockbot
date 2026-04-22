using System.Text.Json;
using RockBot.Tools;
using RockBot.Wisp;

namespace RockBot.Wisp.Tests;

[TestClass]
public class McpStepValidatorTests
{
    private const string GetCalendarEventsSchema = """
        {
          "type": "object",
          "properties": {
            "accountId":   { "type": "string", "description": "The account identifier" },
            "calendarId":  { "type": "string" },
            "timeMin":     { "type": "string" },
            "timeMax":     { "type": "string" },
            "timeZone":    { "type": "string" }
          },
          "required": ["accountId", "calendarId", "timeMin", "timeMax"],
          "additionalProperties": false
        }
        """;

    [TestMethod]
    public void Validate_ValidParams_ReturnsNull()
    {
        var registry = RegistryWith("calendar-mcp", "get_calendar_events", GetCalendarEventsSchema);
        var step = McpStep("calendar-mcp", "get_calendar_events",
            """{"accountId":"a","calendarId":"c","timeMin":"t1","timeMax":"t2"}""");

        var error = McpStepValidator.Validate(step, registry);

        Assert.IsNull(error);
    }

    [TestMethod]
    public void Validate_MissingRequiredField_ReturnsStructuralError()
    {
        var registry = RegistryWith("calendar-mcp", "get_calendar_events", GetCalendarEventsSchema);
        var step = McpStep("calendar-mcp", "get_calendar_events",
            """{"accountId":"a","timeMin":"t1","timeMax":"t2"}""");

        var error = McpStepValidator.Validate(step, registry);

        Assert.IsNotNull(error);
        Assert.AreEqual(FailureCategory.Structural, error.Category);
        StringAssert.Contains(error.Message, "calendarId", "Error should name the missing field");
        StringAssert.Contains(error.Message, "Expected shape", "Error should include schema summary");
        StringAssert.Contains(error.Message, "(required)", "Schema summary should mark required fields");
    }

    [TestMethod]
    public void Validate_UnknownFieldUnderClosedSchema_ReturnsStructuralError()
    {
        // Reproduces the calendar bug: LLM used startDate/endDate (not in schema)
        // and got silently-empty results. With a closed schema we catch this.
        var registry = RegistryWith("calendar-mcp", "get_calendar_events", GetCalendarEventsSchema);
        var step = McpStep("calendar-mcp", "get_calendar_events",
            """{"accountId":"a","calendarId":"c","startDate":"2026-04-23","endDate":"2026-04-23"}""");

        var error = McpStepValidator.Validate(step, registry);

        Assert.IsNotNull(error);
        Assert.AreEqual(FailureCategory.Structural, error.Category);
        StringAssert.Contains(error.Message, "startDate");
        StringAssert.Contains(error.Message, "endDate");
        StringAssert.Contains(error.Message, "timeMin", "Schema summary should show the real fields");
    }

    [TestMethod]
    public void Validate_UnknownFieldUnderOpenSchema_Accepted()
    {
        // No additionalProperties: false → JSON Schema default allows extras.
        const string openSchema = """
            {
              "type": "object",
              "properties": { "accountId": { "type": "string" } },
              "required": ["accountId"]
            }
            """;
        var registry = RegistryWith("server", "tool", openSchema);
        var step = McpStep("server", "tool", """{"accountId":"a","extra":"ok"}""");

        var error = McpStepValidator.Validate(step, registry);

        Assert.IsNull(error);
    }

    [TestMethod]
    public void Validate_NonMcpGateway_ReturnsNull()
    {
        var registry = RegistryWith("any-server", "any-tool", GetCalendarEventsSchema);
        var step = new WispStep
        {
            Id = "s",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Web,
            Tool = "web_search",
            Params = JsonDocument.Parse("""{"query":"x"}""").RootElement
        };

        var error = McpStepValidator.Validate(step, registry);

        Assert.IsNull(error);
    }

    [TestMethod]
    public void Validate_ToolNotRegistered_ReturnsNull()
    {
        // When the MCP tool isn't in the registry (e.g. server not connected),
        // let the existing "tool not registered" failure path produce the error
        // rather than short-circuiting here with a less-informative message.
        var registry = new FakeToolRegistry();
        var step = McpStep("calendar-mcp", "get_calendar_events",
            """{"accountId":"a"}""");

        var error = McpStepValidator.Validate(step, registry);

        Assert.IsNull(error);
    }

    [TestMethod]
    public void Validate_ToolWithNoSchema_ReturnsNull()
    {
        // Some MCP tools register with no schema. We can't validate; skip quietly.
        var registry = new FakeToolRegistry();
        registry.Register(
            new ToolRegistration { Name = "bare", Description = "", Source = "mcp:server", ParametersSchema = null },
            new NoopExecutor());
        var step = McpStep("server", "bare", """{"anything":"goes"}""");

        var error = McpStepValidator.Validate(step, registry);

        Assert.IsNull(error);
    }

    [TestMethod]
    public void Validate_SameToolNameDifferentServer_MatchesOnlyCorrectSource()
    {
        // Two MCP servers could each register a tool named "list_accounts".
        // The validator must match on (server, tool), not tool name alone.
        var registry = new FakeToolRegistry();
        registry.Register(
            new ToolRegistration { Name = "list_accounts", Description = "", Source = "mcp:calendar-mcp",
                ParametersSchema = """{"type":"object","properties":{"calKey":{"type":"string"}},"required":["calKey"],"additionalProperties":false}""" },
            new NoopExecutor());
        registry.Register(
            new ToolRegistration { Name = "list_accounts", Description = "", Source = "mcp:email-mcp",
                ParametersSchema = """{"type":"object","properties":{"mailKey":{"type":"string"}},"required":["mailKey"],"additionalProperties":false}""" },
            new NoopExecutor());

        var step = McpStep("email-mcp", "list_accounts", """{"mailKey":"v"}""");

        var error = McpStepValidator.Validate(step, registry);

        Assert.IsNull(error, "Should validate against email-mcp's schema, not calendar-mcp's");
    }

    [TestMethod]
    public void Validate_MissingParams_TreatedAsEmptyObject()
    {
        // A step with null params and a schema that requires fields should fail.
        var registry = RegistryWith("server", "tool", GetCalendarEventsSchema);
        var step = new WispStep
        {
            Id = "s",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Mcp,
            Server = "server",
            Tool = "tool",
            Params = null
        };

        var error = McpStepValidator.Validate(step, registry);

        Assert.IsNotNull(error);
        StringAssert.Contains(error.Message, "Missing required");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static FakeToolRegistry RegistryWith(string server, string tool, string schema)
    {
        var registry = new FakeToolRegistry();
        registry.Register(
            new ToolRegistration
            {
                Name = tool,
                Description = "",
                Source = $"mcp:{server}",
                ParametersSchema = schema
            },
            new NoopExecutor());
        return registry;
    }

    private static WispStep McpStep(string server, string tool, string paramsJson) =>
        new()
        {
            Id = "s",
            Mode = StepMode.Direct,
            Gateway = GatewayType.Mcp,
            Server = server,
            Tool = tool,
            Params = JsonDocument.Parse(paramsJson).RootElement
        };

    private sealed class NoopExecutor : IToolExecutor
    {
        public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct) =>
            Task.FromResult(new ToolInvokeResponse { ToolCallId = request.ToolCallId, ToolName = request.ToolName, Content = "" });
    }
}
