using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Tools;

namespace RockBot.Host.Tests;

[TestClass]
public class CapabilityClaimVerifierTests
{
    [TestMethod]
    public async Task VerifyAsync_SuccessExpectation_PredicateSucceeds_WhenCallReturnsWithoutError()
    {
        var registry = new StubToolRegistry();
        registry.Executor.NextResponse = new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "mcp_invoke_tool", Content = "ok", IsError = false
        };
        var verifier = NewVerifier(registry);

        var result = await verifier.VerifyAsync(SuccessShape());

        Assert.AreEqual(VerifyOutcome.PredicateSucceeded, result.Outcome);
    }

    [TestMethod]
    public async Task VerifyAsync_SuccessExpectation_PredicateFails_WhenCallReturnsError()
    {
        var registry = new StubToolRegistry();
        registry.Executor.NextResponse = new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "mcp_invoke_tool",
            Content = "Required parameter 'timeZone' was not provided", IsError = true
        };
        var verifier = NewVerifier(registry);

        var result = await verifier.VerifyAsync(SuccessShape());

        Assert.AreEqual(VerifyOutcome.PredicateFailed, result.Outcome);
        StringAssert.Contains(result.Detail!, "timeZone");
    }

    [TestMethod]
    public async Task VerifyAsync_FailurePatternMatches_PredicateSucceeds()
    {
        var registry = new StubToolRegistry();
        registry.Executor.NextResponse = new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "mcp_invoke_tool",
            Content = "MCP server timed out after 30s", IsError = true
        };
        var verifier = NewVerifier(registry);

        var shape = SuccessShape() with
        {
            Expect = new VerifyExpectation(VerifyExpectationKind.FailureWithMessage, "timed out")
        };

        var result = await verifier.VerifyAsync(shape);

        Assert.AreEqual(VerifyOutcome.PredicateSucceeded, result.Outcome);
    }

    [TestMethod]
    public async Task VerifyAsync_FailurePatternMissingFromError_PredicateFails()
    {
        var registry = new StubToolRegistry();
        registry.Executor.NextResponse = new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "mcp_invoke_tool",
            Content = "Some other failure mode", IsError = true
        };
        var verifier = NewVerifier(registry);

        var shape = SuccessShape() with
        {
            Expect = new VerifyExpectation(VerifyExpectationKind.FailureWithMessage, "timed out")
        };

        var result = await verifier.VerifyAsync(shape);

        Assert.AreEqual(VerifyOutcome.PredicateFailed, result.Outcome);
    }

    [TestMethod]
    public async Task VerifyAsync_FailurePatternExpected_ButCallSucceeded_PredicateFails()
    {
        var registry = new StubToolRegistry();
        registry.Executor.NextResponse = new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "mcp_invoke_tool", Content = "ok", IsError = false
        };
        var verifier = NewVerifier(registry);

        var shape = SuccessShape() with
        {
            Expect = new VerifyExpectation(VerifyExpectationKind.FailureWithMessage, "timed out")
        };

        var result = await verifier.VerifyAsync(shape);

        Assert.AreEqual(VerifyOutcome.PredicateFailed, result.Outcome);
    }

    [TestMethod]
    public async Task VerifyAsync_GatewayThrows_ReturnsUncertain()
    {
        var registry = new StubToolRegistry();
        registry.Executor.NextThrow = new InvalidOperationException("bridge offline");
        var verifier = NewVerifier(registry);

        var result = await verifier.VerifyAsync(SuccessShape());

        Assert.AreEqual(VerifyOutcome.Uncertain, result.Outcome);
        StringAssert.Contains(result.Detail!, "bridge offline");
    }

    [TestMethod]
    public async Task VerifyAsync_McpInvokeToolNotRegistered_ReturnsUncertain()
    {
        var registry = new StubToolRegistry { ReturnExecutor = false };
        var verifier = NewVerifier(registry);

        var result = await verifier.VerifyAsync(SuccessShape());

        Assert.AreEqual(VerifyOutcome.Uncertain, result.Outcome);
        StringAssert.Contains(result.Detail!, "mcp_invoke_tool");
    }

    [TestMethod]
    public async Task VerifyAsync_BudgetExceeded_ReturnsUncertain()
    {
        var registry = new StubToolRegistry();
        registry.Executor.DelayBeforeReturn = TimeSpan.FromSeconds(2);
        registry.Executor.NextResponse = new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "mcp_invoke_tool", Content = "ok", IsError = false
        };
        var verifier = NewVerifier(registry, budget: TimeSpan.FromMilliseconds(50));

        var result = await verifier.VerifyAsync(SuccessShape());

        Assert.AreEqual(VerifyOutcome.Uncertain, result.Outcome);
        StringAssert.Contains(result.Detail!, "budget");
    }

    [TestMethod]
    public async Task VerifyAsync_CallerCancellation_PropagatesAsCancellation()
    {
        var registry = new StubToolRegistry();
        registry.Executor.DelayBeforeReturn = TimeSpan.FromSeconds(2);
        registry.Executor.NextResponse = new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "mcp_invoke_tool", Content = "ok", IsError = false
        };
        var verifier = NewVerifier(registry, budget: TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // TaskCanceledException is a subtype of OperationCanceledException, so assert on the base type.
        try
        {
            await verifier.VerifyAsync(SuccessShape(), cts.Token);
            Assert.Fail("Expected the caller cancellation to propagate as an OperationCanceledException.");
        }
        catch (OperationCanceledException)
        {
            // Expected — caller cancellation propagated rather than being swallowed as Uncertain.
        }
    }

    [TestMethod]
    public async Task VerifyAsync_CachesResult_WithinTtl()
    {
        var registry = new StubToolRegistry();
        registry.Executor.NextResponse = new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "mcp_invoke_tool", Content = "ok", IsError = false
        };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 8, 15, 0, 0, TimeSpan.Zero));
        var verifier = NewVerifier(registry, time: time, ttl: TimeSpan.FromMinutes(5));

        var shape = SuccessShape();

        var first = await verifier.VerifyAsync(shape);
        var executionsAfterFirst = registry.Executor.ExecutionCount;
        var second = await verifier.VerifyAsync(shape);

        Assert.AreEqual(VerifyOutcome.PredicateSucceeded, first.Outcome);
        Assert.AreEqual(VerifyOutcome.PredicateSucceeded, second.Outcome);
        Assert.AreEqual(1, executionsAfterFirst, "First call should hit the gateway.");
        Assert.AreEqual(1, registry.Executor.ExecutionCount, "Second call within TTL should be served from cache.");
    }

    [TestMethod]
    public async Task VerifyAsync_CacheExpires_AfterTtl()
    {
        var registry = new StubToolRegistry();
        registry.Executor.NextResponse = new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "mcp_invoke_tool", Content = "ok", IsError = false
        };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 8, 15, 0, 0, TimeSpan.Zero));
        var verifier = NewVerifier(registry, time: time, ttl: TimeSpan.FromMinutes(5));

        var shape = SuccessShape();

        await verifier.VerifyAsync(shape);
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await verifier.VerifyAsync(shape);

        Assert.AreEqual(2, registry.Executor.ExecutionCount,
            "Call after TTL expiry should re-hit the gateway.");
    }

    [TestMethod]
    public async Task VerifyAsync_DifferentShapes_DoNotShareCache()
    {
        var registry = new StubToolRegistry();
        registry.Executor.NextResponse = new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "mcp_invoke_tool", Content = "ok", IsError = false
        };
        var verifier = NewVerifier(registry);

        await verifier.VerifyAsync(SuccessShape() with { Tool = "tool_a" });
        await verifier.VerifyAsync(SuccessShape() with { Tool = "tool_b" });

        Assert.AreEqual(2, registry.Executor.ExecutionCount,
            "Different verify shapes must not share a cache slot.");
    }

    [TestMethod]
    public async Task VerifyAsync_SerializesArgumentsAsNestedField_ForMcpInvokeTool()
    {
        var registry = new StubToolRegistry();
        registry.Executor.NextResponse = new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "mcp_invoke_tool", Content = "ok", IsError = false
        };
        var verifier = NewVerifier(registry);

        await verifier.VerifyAsync(SuccessShape());

        Assert.IsNotNull(registry.Executor.LastRequest);
        var doc = JsonDocument.Parse(registry.Executor.LastRequest!.Arguments!);
        Assert.AreEqual("calendar-mcp", doc.RootElement.GetProperty("server_name").GetString());
        Assert.AreEqual("get_calendar_events", doc.RootElement.GetProperty("tool_name").GetString());
        Assert.IsTrue(doc.RootElement.TryGetProperty("arguments", out var args));
        Assert.AreEqual(JsonValueKind.Object, args.ValueKind);
        Assert.AreEqual("America/Chicago", args.GetProperty("timeZone").GetString());
    }

    // --- helpers -------------------------------------------------------------

    private static CapabilityClaimVerifier NewVerifier(
        StubToolRegistry registry,
        TimeProvider? time = null,
        TimeSpan? ttl = null,
        TimeSpan? budget = null) =>
        new(registry,
            NullLogger<CapabilityClaimVerifier>.Instance,
            time ?? TimeProvider.System,
            ttl,
            budget);

    private static VerifyShape SuccessShape() => new(
        Server: "calendar-mcp",
        Tool: "get_calendar_events",
        Arguments: JsonDocument.Parse("""{"accountId":"x","timeZone":"America/Chicago"}""").RootElement,
        Expect: new VerifyExpectation(VerifyExpectationKind.Success));

    private sealed class StubToolRegistry : IToolRegistry
    {
        public StubToolExecutor Executor { get; } = new();
        public bool ReturnExecutor { get; set; } = true;

        public IReadOnlyList<ToolRegistration> GetTools() => [];
        public IToolExecutor? GetExecutor(string toolName) => ReturnExecutor ? Executor : null;
        public void Register(ToolRegistration registration, IToolExecutor executor) { }
        public bool Unregister(string toolName) => false;
    }

    private sealed class StubToolExecutor : IToolExecutor
    {
        public ToolInvokeResponse? NextResponse { get; set; }
        public Exception? NextThrow { get; set; }
        public TimeSpan DelayBeforeReturn { get; set; }
        public ToolInvokeRequest? LastRequest { get; private set; }
        public int ExecutionCount { get; private set; }

        public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
        {
            LastRequest = request;
            ExecutionCount++;

            if (DelayBeforeReturn > TimeSpan.Zero)
                await Task.Delay(DelayBeforeReturn, ct);

            if (NextThrow is not null)
                throw NextThrow;

            return NextResponse ?? throw new InvalidOperationException("Test forgot to set NextResponse");
        }
    }

    /// <summary>Minimal fake <see cref="TimeProvider"/> for TTL-expiry testing without the test-package dependency.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
