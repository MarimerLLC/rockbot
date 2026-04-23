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
        CreateExecutor(ILlmClient? llmClient)
    {
        var registry = new FakeToolRegistry();
        var memory = new FakeWorkingMemory();
        var options = new WispOptions { SharedVolumePath = null };
        var captured = new List<string>();
        var executor = new WispExecutor(registry, memory, agentLoopRunner: null!, options,
            NullLogger<WispExecutor>.Instance, llmClient);
        return (executor, registry, captured);
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
