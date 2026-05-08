using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests.Recovery;

/// <summary>
/// Phase 2 wire-in: McpRecoveryExecutor emits a <see cref="CapabilityClaim"/> via
/// <see cref="ICapabilityClaimWriter"/> when recovery has been attempted but
/// exhausted. Skipped paths: the no-provider/no-StageB short-circuit (recovery
/// never actually attempted anything).
/// </summary>
[TestClass]
public class McpRecoveryExecutorCapabilityClaimTests
{
    [TestMethod]
    public async Task StageA_RetryFailed_EmitsCapabilityClaim()
    {
        // Recovery resolves a value via Stage A but the retried call still fails.
        var writer = new RecordingClaimWriter();
        var provider = new FakeProvider(
            (_, _, f) => f == "timeZone",
            _ => new ResolvedDefault("America/Chicago"));

        var calls = 0;
        McpInvokeDelegate invoke = (r, h, ct) =>
        {
            calls++;
            // First retry returns an unrelated failure that doesn't chain.
            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = r.ToolCallId, ToolName = r.ToolName,
                Content = "permission denied", IsError = true
            });
        };

        var exec = new McpRecoveryExecutor(
            invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance,
            stageB: null, capabilityClaimWriter: writer);

        var req = new ToolInvokeRequest
        {
            ToolCallId = "1", ToolName = "get_calendar_events",
            Arguments = """{"accountId":"x"}"""
        };
        var failed = Err(req, "Required parameter 'timeZone' was not provided");

        var result = await exec.RecoverAsync("calendar-mcp", "get_calendar_events", req, failed, default);

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(1, writer.Saved.Count, "Phase 2 claim must be emitted on Stage A retry-failure.");
        var claim = writer.Saved[0];
        Assert.AreEqual("calendar-mcp", claim.Server);
        Assert.AreEqual("get_calendar_events", claim.Tool);
        StringAssert.Contains(claim.Statement, "Stage A");
        StringAssert.Contains(claim.Statement, "timeZone");
        Assert.AreEqual(VerifyExpectationKind.Success, claim.Verify.Expect.Kind,
            "Claim must be falsifiable: a future successful call evicts it.");
    }

    [TestMethod]
    public async Task StageB_FillFails_EmitsCapabilityClaim()
    {
        var writer = new RecordingClaimWriter();
        McpInvokeDelegate invoke = (r, h, ct) => Task.FromResult(Ok(r, "ok"));
        var stageB = new NullFillingFiller();

        var exec = new McpRecoveryExecutor(
            invoke, [], NullLogger<McpRecoveryExecutor>.Instance,
            stageB: stageB.Filler, capabilityClaimWriter: writer);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "do", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'novelField'");

        var result = await exec.RecoverAsync("synthetic", "do", req, failed, default);

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(1, writer.Saved.Count, "Phase 2 claim must be emitted when Stage B can't fill the field.");
        StringAssert.Contains(writer.Saved[0].Statement, "Stage B");
        StringAssert.Contains(writer.Saved[0].Statement, "novelField");
    }

    [TestMethod]
    public async Task StageB_RetryFailed_EmitsCapabilityClaim()
    {
        var writer = new RecordingClaimWriter();
        // Stage B fills, but the retried call still returns a non-chainable error.
        McpInvokeDelegate invoke = (r, h, ct) => Task.FromResult(Err(r, "Server unreachable"));
        var stageB = new CannedFillingFiller("\"some-value\"");

        var exec = new McpRecoveryExecutor(
            invoke, [], NullLogger<McpRecoveryExecutor>.Instance,
            stageB: stageB.Filler, capabilityClaimWriter: writer);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "do", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'foo'");

        var result = await exec.RecoverAsync("synthetic", "do", req, failed, default);

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(1, writer.Saved.Count);
        StringAssert.Contains(writer.Saved[0].Statement, "Stage B");
        StringAssert.Contains(writer.Saved[0].Statement, "foo");
    }

    [TestMethod]
    public async Task NoProviderNoStageB_DoesNotEmitClaim_BecauseRecoveryNeverAttempted()
    {
        var writer = new RecordingClaimWriter();
        McpInvokeDelegate invoke = (r, h, ct) => Task.FromResult(Ok(r, "ok"));

        var exec = new McpRecoveryExecutor(
            invoke, [], NullLogger<McpRecoveryExecutor>.Instance,
            stageB: null, capabilityClaimWriter: writer);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "do", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'fieldNobodyHandles'");

        await exec.RecoverAsync("synthetic", "do", req, failed, default);

        Assert.AreEqual(0, writer.Saved.Count,
            "Recovery never tried anything (no provider, Stage B disabled) — no claim.");
    }

    [TestMethod]
    public async Task SuccessfulRecovery_DoesNotEmitClaim()
    {
        var writer = new RecordingClaimWriter();
        var provider = new FakeProvider(
            (_, _, f) => f == "timeZone",
            _ => new ResolvedDefault("America/Chicago"));

        McpInvokeDelegate invoke = (r, h, ct) => Task.FromResult(Ok(r, "events: []"));

        var exec = new McpRecoveryExecutor(
            invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance,
            stageB: null, capabilityClaimWriter: writer);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'timeZone' was not provided");

        var result = await exec.RecoverAsync("srv", "get", req, failed, default);

        Assert.IsFalse(result.IsError);
        Assert.AreEqual(0, writer.Saved.Count, "A successful recovery must NOT emit a claim.");
    }

    [TestMethod]
    public async Task NoClaimWriter_RecoveryStillWorks()
    {
        // Wire-in is optional — recovery must continue to function when no writer is registered.
        McpInvokeDelegate invoke = (r, h, ct) => Task.FromResult(Err(r, "permission denied"));
        var provider = new FakeProvider(
            (_, _, f) => f == "timeZone",
            _ => new ResolvedDefault("America/Chicago"));

        var exec = new McpRecoveryExecutor(
            invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance,
            stageB: null, capabilityClaimWriter: null);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'timeZone' was not provided");

        var result = await exec.RecoverAsync("srv", "get", req, failed, default);

        // Recovery still runs and surfaces the annotated trail; no exception.
        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "retry-failed");
    }

    [TestMethod]
    public async Task EmittedClaim_PreservesOriginalArguments_AsVerifyShape()
    {
        var writer = new RecordingClaimWriter();
        // Provider matches the missing field so recovery actually attempts a retry.
        var provider = new FakeProvider(
            (_, _, f) => f == "startDate",
            _ => new ResolvedDefault("2026-05-08"));

        // Retry returns a non-chainable error → triggers Stage A retry-failed path.
        McpInvokeDelegate invoke = (r, h, ct) => Task.FromResult(Err(r, "permission denied"));

        var exec = new McpRecoveryExecutor(
            invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance,
            stageB: null, capabilityClaimWriter: writer);

        var req = new ToolInvokeRequest
        {
            ToolCallId = "1", ToolName = "get_calendar_events",
            Arguments = """{"accountId":"work","timeZone":"UTC"}"""
        };
        var failed = Err(req, "Required parameter 'startDate' was not provided");

        await exec.RecoverAsync("calendar-mcp", "get_calendar_events", req, failed, default);

        Assert.AreEqual(1, writer.Saved.Count);
        var claim = writer.Saved[0];
        // VerifyShape carries the original call shape — so the next session's verifier
        // can replay the call and confirm whether the limitation still holds.
        Assert.AreEqual("calendar-mcp", claim.Verify.Server);
        Assert.AreEqual("get_calendar_events", claim.Verify.Tool);
        Assert.AreEqual("work", claim.Verify.Arguments.GetProperty("accountId").GetString());
        Assert.AreEqual("UTC", claim.Verify.Arguments.GetProperty("timeZone").GetString());
    }

    [TestMethod]
    public async Task ClaimWriterThrows_DoesNotBreakRecoveryPath()
    {
        var writer = new ThrowingClaimWriter();
        var provider = new FakeProvider(
            (_, _, f) => f == "timeZone",
            _ => new ResolvedDefault("America/Chicago"));
        McpInvokeDelegate invoke = (r, h, ct) => Task.FromResult(Err(r, "permission denied"));

        var exec = new McpRecoveryExecutor(
            invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance,
            stageB: null, capabilityClaimWriter: writer);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'timeZone' was not provided");

        // Must not bubble the writer's exception — recovery is the priority path.
        var result = await exec.RecoverAsync("srv", "get", req, failed, default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "retry-failed");
    }

    // --- helpers -------------------------------------------------------------

    private static ToolInvokeResponse Err(ToolInvokeRequest req, string content) => new()
    {
        ToolCallId = req.ToolCallId, ToolName = req.ToolName,
        Content = content, IsError = true
    };

    private static ToolInvokeResponse Ok(ToolInvokeRequest req, string content) => new()
    {
        ToolCallId = req.ToolCallId, ToolName = req.ToolName,
        Content = content, IsError = false
    };

    private sealed class FakeProvider(
        Func<string, string, string, bool> match,
        Func<ResolveContext, ResolvedDefault?> resolve) : IToolArgumentDefaultsProvider
    {
        public bool CanResolve(string s, string t, string f) => match(s, t, f);
        public Task<ResolvedDefault?> ResolveAsync(ResolveContext ctx, CancellationToken ct) =>
            Task.FromResult(resolve(ctx));
    }

    private sealed class RecordingClaimWriter : ICapabilityClaimWriter
    {
        public List<CapabilityClaim> Saved { get; } = new();
        public Task SaveCapabilityClaimAsync(CapabilityClaim claim, CancellationToken cancellationToken = default)
        {
            Saved.Add(claim);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingClaimWriter : ICapabilityClaimWriter
    {
        public Task SaveCapabilityClaimAsync(CapabilityClaim claim, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("ltm offline");
    }

    private sealed class NullFillingFiller
    {
        public StageBLlmFiller Filler { get; } = new TestFiller();
        private sealed class TestFiller() : StageBLlmFiller(
            new NoopLlmClient(), NullLogger<StageBLlmFiller>.Instance)
        {
            public override Task<object?> TryFillAsync(
                string serverName, string toolName, string fieldName,
                IReadOnlyDictionary<string, object?> existingArgs,
                string? originalErrorText, CancellationToken ct) =>
                Task.FromResult<object?>(null);
        }
    }

    private sealed class CannedFillingFiller
    {
        public StageBLlmFiller Filler { get; }
        public CannedFillingFiller(string canned)
        {
            Filler = new TestFiller(canned);
        }
        private sealed class TestFiller(string? canned) : StageBLlmFiller(
            new NoopLlmClient(), NullLogger<StageBLlmFiller>.Instance)
        {
            public override Task<object?> TryFillAsync(
                string serverName, string toolName, string fieldName,
                IReadOnlyDictionary<string, object?> existingArgs,
                string? originalErrorText, CancellationToken ct)
            {
                if (canned is null) return Task.FromResult<object?>(null);
                var doc = JsonDocument.Parse(canned);
                return Task.FromResult<object?>(McpToolExecutor.ConvertJsonElement(doc.RootElement));
            }
        }
    }

    private sealed class NoopLlmClient : RockBot.Host.ILlmClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            RockBot.Host.ModelTier tier,
            Microsoft.Extensions.AI.ChatOptions? options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
