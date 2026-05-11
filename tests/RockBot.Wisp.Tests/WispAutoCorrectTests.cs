using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Wisp;

namespace RockBot.Wisp.Tests;

[TestClass]
public class WispAutoCorrectTests
{
    private const string CalendarSchema = """
        {
          "type": "object",
          "properties": {
            "accountId":  { "type": "string" },
            "calendarId": { "type": "string" },
            "timeMin":    { "type": "string" },
            "timeMax":    { "type": "string" }
          },
          "required": ["accountId", "calendarId", "timeMin", "timeMax"],
          "additionalProperties": false
        }
        """;

    [TestMethod]
    public async Task ValidationFailure_AutoCorrectionRewritesParams_StepSucceeds()
    {
        // LLM rewrites bad params (startDate/endDate) into the real schema.
        var llm = new ScriptedLlmClient("""
            {"accountId":"a","calendarId":"c","timeMin":"2026-04-23T00:00:00","timeMax":"2026-04-23T23:59:59"}
            """);

        var (executor, registry, captured) = CreateExecutor(llm);
        RegisterCalendarMcp(registry, captured);

        var definition = MakeCalendarWisp(
            """{"accountId":"a","calendarId":"c","startDate":"2026-04-23","endDate":"2026-04-23"}""");

        var result = await executor.ExecuteAsync(definition, "wisp-ac-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, "Step should succeed after auto-correction");
        Assert.AreEqual(1, llm.CallCount);

        // The executor was called with the corrected params wrapped in mcp_invoke_tool args.
        Assert.AreEqual(1, captured.Count);
        var args = JsonDocument.Parse(captured[0]).RootElement;
        var innerArgs = args.GetProperty("arguments");
        Assert.IsTrue(innerArgs.TryGetProperty("timeMin", out _),
            "Corrected params should contain 'timeMin'");
        Assert.IsFalse(innerArgs.TryGetProperty("startDate", out _),
            "Corrected params should not contain 'startDate'");
    }

    [TestMethod]
    public async Task ValidationFailure_LlmReturnsInvalidJson_OriginalErrorBubbled()
    {
        var llm = new ScriptedLlmClient("this is not JSON");
        var (executor, registry, _) = CreateExecutor(llm);
        RegisterCalendarMcp(registry, capturedArgs: null);

        var definition = MakeCalendarWisp(
            """{"accountId":"a","calendarId":"c","startDate":"x","endDate":"y"}""");

        var result = await executor.ExecuteAsync(definition, "wisp-ac-2", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.FailedStep!.Error!.Category);
        StringAssert.Contains(result.FailedStep.Error.Message, "startDate",
            "Original validation error (naming the unknown field) should be preserved");
    }

    [TestMethod]
    public async Task ValidationFailure_LlmReturnsStillInvalidParams_OriginalErrorBubbled()
    {
        // LLM emits syntactically-valid JSON but it still misses `calendarId`.
        // We don't trust the LLM's output — a re-validation catches this.
        var llm = new ScriptedLlmClient("""
            {"accountId":"a","timeMin":"t1","timeMax":"t2"}
            """);
        var (executor, registry, _) = CreateExecutor(llm);
        RegisterCalendarMcp(registry, capturedArgs: null);

        var definition = MakeCalendarWisp(
            """{"accountId":"a","startDate":"x"}""");

        var result = await executor.ExecuteAsync(definition, "wisp-ac-3", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.FailedStep!.Error!.Category);
    }

    [TestMethod]
    public async Task ValidationFailure_NoLlmClientWired_OriginalErrorBubbled()
    {
        // With no ILlmClient injected, the auto-correction path is a no-op.
        var (executor, registry, _) = CreateExecutor(llmClient: null);
        RegisterCalendarMcp(registry, capturedArgs: null);

        var definition = MakeCalendarWisp(
            """{"accountId":"a","startDate":"x"}""");

        var result = await executor.ExecuteAsync(definition, "wisp-ac-4", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.FailedStep!.Error!.Category);
    }

    [TestMethod]
    public async Task ValidationFailure_LlmReturnsNoCorrectionSentinel_OriginalErrorBubbled()
    {
        // When a required field is genuinely absent from the current params (no
        // remappable equivalent), the rewriter must refuse to invent a value and
        // instead emit NO_CORRECTION so the main agent can fetch the missing info.
        var llm = new ScriptedLlmClient("NO_CORRECTION");
        var (executor, registry, _) = CreateExecutor(llm);
        RegisterCalendarMcp(registry, capturedArgs: null);

        // Missing accountId entirely — no way for the rewriter to fill it honestly.
        var definition = MakeCalendarWisp(
            """{"calendarId":"c","timeMin":"t1","timeMax":"t2"}""");

        var result = await executor.ExecuteAsync(definition, "wisp-ac-6", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.FailedStep!.Error!.Category);
        StringAssert.Contains(result.FailedStep.Error.Message, "accountId",
            "Original validation error should be surfaced so the caller can fetch the missing field");
        Assert.AreEqual(1, llm.CallCount, "LLM was called (and declined), not skipped");
    }

    [TestMethod]
    public async Task PreflightRecovery_FillsEnvDefault_NoLlmCallNeeded()
    {
        // When preflight recovery can silently fill every missing field from an
        // environmental default, the step proceeds without an LLM round-trip.
        var llm = new ScriptedLlmClient("UNUSED");
        var preflight = new StubPreflightRecovery(
            filledDefaults: new Dictionary<string, object?> { ["calendarId"] = "primary" },
            unresolved: [],
            enrichedContext: null);

        var (executor, registry, captured) = CreateExecutor(llm, preflight);
        RegisterCalendarMcp(registry, captured);

        // Missing calendarId — preflight will fill it.
        var definition = MakeCalendarWisp(
            """{"accountId":"a","timeMin":"t1","timeMax":"t2"}""");

        var result = await executor.ExecuteAsync(definition, "wisp-pf-1", parentSessionId: "parent-42", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, "Step should succeed after preflight env fill");
        Assert.AreEqual(0, llm.CallCount, "LLM auto-correct must not run when preflight filled every missing field");

        // Verify the filled value flowed through to the executor.
        Assert.AreEqual(1, captured.Count);
        var args = JsonDocument.Parse(captured[0]).RootElement;
        var innerArgs = args.GetProperty("arguments");
        Assert.AreEqual("primary", innerArgs.GetProperty("calendarId").GetString());

        // Verify the wisp threaded parent session id to preflight recovery.
        Assert.AreEqual("parent-42", preflight.LastParentSessionId);
    }

    [TestMethod]
    public async Task PreflightRecovery_PartialFill_EnrichmentInjectedIntoLlmPrompt()
    {
        // Preflight fills one missing field, leaves another unresolved with an
        // enriched-error context. The auto-correct LLM gets the enriched context
        // appended to its prompt, then supplies the final value.
        var llm = new CapturingScriptedLlmClient("""
            {"accountId":"acct-from-enriched","calendarId":"primary","timeMin":"t1","timeMax":"t2"}
            """);
        var preflight = new StubPreflightRecovery(
            filledDefaults: new Dictionary<string, object?> { ["calendarId"] = "primary" },
            unresolved: ["accountId"],
            enrichedContext: "Field schema: accountId. Recent successful calls in this session: list_accounts");

        var (executor, registry, captured) = CreateExecutor(llm, preflight);
        RegisterCalendarMcp(registry, captured);

        var definition = MakeCalendarWisp(
            """{"timeMin":"t1","timeMax":"t2"}""");

        var result = await executor.ExecuteAsync(definition, "wisp-pf-2", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, "Step should succeed after partial fill + LLM completion");
        Assert.AreEqual(1, llm.CallCount);
        StringAssert.Contains(llm.LastPrompt!, "Recent successful calls in this session: list_accounts",
            "Enriched context from preflight recovery must appear in the LLM correction prompt");
        StringAssert.Contains(llm.LastPrompt!, "Recovery context",
            "The prompt must introduce the recovery-context block so the LLM knows to use it");
    }

    private const string CalendarSchemaWithTimeZone = """
        {
          "type": "object",
          "properties": {
            "accountId":  { "type": "string" },
            "calendarId": { "type": "string" },
            "timeMin":    { "type": "string" },
            "timeMax":    { "type": "string" },
            "timeZone":   { "type": "string" }
          },
          "required": ["accountId", "calendarId", "timeMin", "timeMax", "timeZone"],
          "additionalProperties": false
        }
        """;

    [TestMethod]
    public async Task PreflightRecovery_SchemaFromRecoveryHook_BridgeModeFill()
    {
        // Regression: in bridge-mode agents the per-server MCP tool registrations
        // don't live in the local tool registry, so McpStepValidator.Validate would
        // historically return null (skip validation) and the wisp would only recover
        // via the slower post-flight path. ValidateDetailedAsync now consults the
        // IMcpPreflightRecovery hook for the schema as a fallback — verify that path
        // engages, the env-default fill fires, and the LLM is never called.
        var llm = new ScriptedLlmClient("UNUSED");
        var preflight = new StubPreflightRecovery(
            filledDefaults: new Dictionary<string, object?> { ["timeZone"] = "America/Chicago" },
            unresolved: [],
            enrichedContext: null,
            schemaJson: CalendarSchemaWithTimeZone);

        var (executor, registry, captured) = CreateExecutor(llm, preflight);

        // Register ONLY mcp_invoke_tool (the bridge-mode case) — NO mcp:calendar-mcp
        // tool registration, so McpStepValidator.FindMcpTool returns null and the
        // validator has to lean on the preflight hook for the schema.
        registry.Register(
            new ToolRegistration
            {
                Name = "mcp_invoke_tool",
                Description = "",
                Source = "mcp:management"
            },
            new CapturingToolExecutor(r => captured.Add(r.Arguments ?? ""), "{\"events\":[]}"));

        // Schema requires timeZone but the wisp omits it.
        var definition = MakeCalendarWisp(
            """{"accountId":"a","calendarId":"c","timeMin":"t1","timeMax":"t2"}""");

        var result = await executor.ExecuteAsync(definition, "wisp-bridge-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, "Step should succeed via bridge-mode schema fallback");
        Assert.AreEqual(0, llm.CallCount, "LLM auto-correct must not run");
        Assert.IsTrue(preflight.SchemaLookupCount >= 1,
            "Validator must have consulted the schema source");

        // Verify the env default landed in the wire call.
        Assert.AreEqual(1, captured.Count, $"captured args: {string.Join(" | ", captured)}");
        var args = JsonDocument.Parse(captured[0]).RootElement;
        var innerArgs = args.GetProperty("arguments");
        Assert.AreEqual("America/Chicago", innerArgs.GetProperty("timeZone").GetString());
    }

    [TestMethod]
    public async Task PreflightRecovery_NotWired_FallsBackToBareAutoCorrect()
    {
        // Without an IMcpPreflightRecovery in DI the wisp keeps its prior
        // behaviour: bare LLM auto-correct with just the validation error.
        var llm = new CapturingScriptedLlmClient("""
            {"accountId":"a","calendarId":"c","timeMin":"2026-04-23T00:00:00","timeMax":"2026-04-23T23:59:59"}
            """);
        var (executor, registry, captured) = CreateExecutor(llm, preflightRecovery: null);
        RegisterCalendarMcp(registry, captured);

        var definition = MakeCalendarWisp(
            """{"accountId":"a","calendarId":"c","startDate":"2026-04-23","endDate":"2026-04-23"}""");

        var result = await executor.ExecuteAsync(definition, "wisp-pf-3", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(llm.LastPrompt);
        Assert.IsFalse(llm.LastPrompt!.Contains("Recovery context"),
            "Recovery context block must be absent when no preflight recovery is wired");
    }

    [TestMethod]
    public async Task ValidationFailure_LlmWrapsResponseInCodeFences_StillParsed()
    {
        var llm = new ScriptedLlmClient("""
            ```json
            {"accountId":"a","calendarId":"c","timeMin":"t1","timeMax":"t2"}
            ```
            """);
        var (executor, registry, captured) = CreateExecutor(llm);
        RegisterCalendarMcp(registry, captured);

        var definition = MakeCalendarWisp(
            """{"accountId":"a","calendarId":"c","startDate":"x","endDate":"y"}""");

        var result = await executor.ExecuteAsync(definition, "wisp-ac-5", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, "Code-fenced JSON from the LLM should still be accepted");
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private static (WispExecutor Executor, FakeToolRegistry Registry, List<string> CapturedArgs)
        CreateExecutor(ILlmClient? llmClient,
                       IMcpPreflightRecovery? preflightRecovery = null)
    {
        var registry = new FakeToolRegistry();
        var memory = new FakeWorkingMemory();
        var options = new WispOptions { SharedVolumePath = null };
        var captured = new List<string>();
        var executor = new WispExecutor(registry, memory, agentLoopRunner: null!, options,
            NullLogger<WispExecutor>.Instance, llmClient,
            a2aCanceller: null, preflightRecovery: preflightRecovery);
        return (executor, registry, captured);
    }

    private sealed class StubPreflightRecovery(
        IReadOnlyDictionary<string, object?> filledDefaults,
        IReadOnlyList<string> unresolved,
        string? enrichedContext,
        string? schemaJson = null) : IMcpPreflightRecovery
    {
        public string? LastParentSessionId { get; private set; }
        public IReadOnlyList<string>? LastMissingFields { get; private set; }
        public int SchemaLookupCount { get; private set; }

        public Task<PreflightRecoveryResult> TryRecoverAsync(
            string serverName,
            string toolName,
            IReadOnlyList<string> missingFields,
            IReadOnlyDictionary<string, object?> existingArgs,
            string? parentSessionId,
            CancellationToken ct)
        {
            LastParentSessionId = parentSessionId;
            LastMissingFields = missingFields;
            return Task.FromResult(
                new PreflightRecoveryResult(filledDefaults, unresolved, enrichedContext));
        }

        public Task<string?> TryGetParametersSchemaAsync(
            string serverName, string toolName, CancellationToken ct)
        {
            SchemaLookupCount++;
            return Task.FromResult(schemaJson);
        }
    }

    private sealed class CapturingScriptedLlmClient(string responseText) : ILlmClient
    {
        public int CallCount { get; private set; }
        public string? LastPrompt { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Respond(messages);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Respond(messages);

        private Task<ChatResponse> Respond(IEnumerable<ChatMessage> messages)
        {
            CallCount++;
            LastPrompt = messages.LastOrDefault()?.Text ?? "";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }
    }

    private static void RegisterCalendarMcp(FakeToolRegistry registry, List<string>? capturedArgs)
    {
        // mcp_invoke_tool is the wrapper the wisp router invokes for Mcp gateway steps.
        registry.Register(
            new ToolRegistration
            {
                Name = "mcp_invoke_tool",
                Description = "",
                Source = "mcp-management"
            },
            capturedArgs is not null
                ? new CapturingToolExecutor(r => capturedArgs.Add(r.Arguments ?? ""), "{\"events\":[]}")
                : new FakeToolExecutor("{\"events\":[]}"));

        // The target MCP tool whose schema the validator consults.
        registry.Register(
            new ToolRegistration
            {
                Name = "get_calendar_events",
                Description = "",
                Source = "mcp:calendar-mcp",
                ParametersSchema = CalendarSchema
            },
            new FakeToolExecutor("{\"events\":[]}"));
    }

    private static WispDefinition MakeCalendarWisp(string paramsJson) => new()
    {
        Description = "calendar fetch",
        Steps =
        [
            new WispStep
            {
                Id = "get",
                Mode = StepMode.Direct,
                Gateway = GatewayType.Mcp,
                Server = "calendar-mcp",
                Tool = "get_calendar_events",
                Params = JsonDocument.Parse(paramsJson).RootElement
            }
        ]
    };

    private sealed class ScriptedLlmClient(string responseText) : ILlmClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }
    }
}
