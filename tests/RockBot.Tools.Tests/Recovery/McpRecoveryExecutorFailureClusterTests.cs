using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests.Recovery;

/// <summary>
/// Phase 5 wire-in: the recovery executor records every post-recovery failure
/// into <see cref="IFailureClusterStore"/>. Auto-recovered calls do NOT record.
/// </summary>
[TestClass]
public class McpRecoveryExecutorFailureClusterTests
{
    [TestMethod]
    public async Task AutoRecovered_StageA_DoesNotRecord()
    {
        var store = new RecordingClusterStore();
        var provider = new FakeProvider(
            (_, _, f) => f == "timeZone",
            _ => new ResolvedDefault("America/Chicago"));
        // Retry succeeds — recovery successful, nothing recorded.
        McpInvokeDelegate invoke = (r, _, _) => Task.FromResult(Ok(r, "events: []"));

        var exec = new McpRecoveryExecutor(
            invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance,
            failureClusterStore: store);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'timeZone' was not provided");

        var result = await exec.RecoverAsync("srv", "get", req, failed, default,
            sessionId: "session-A");

        Assert.IsFalse(result.IsError);
        Assert.AreEqual(0, store.Records.Count, "auto-recovered calls must not enter the cluster store");
    }

    [TestMethod]
    public async Task StageA_RetryFailed_RecordsClusterWithFieldClassAndSessionId()
    {
        var store = new RecordingClusterStore();
        var provider = new FakeProvider(
            (_, _, f) => f == "timeZone",
            _ => new ResolvedDefault("America/Chicago"));
        // Retry surfaces a non-schema failure that doesn't chain.
        McpInvokeDelegate invoke = (r, _, _) =>
            Task.FromResult(Err(r, "permission denied"));

        var exec = new McpRecoveryExecutor(
            invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance,
            failureClusterStore: store);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get_calendar_events", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'timeZone' was not provided");

        var result = await exec.RecoverAsync(
            "calendar-mcp", "get_calendar_events", req, failed, default,
            sessionId: "session-A");

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(1, store.Records.Count);
        var rec = store.Records[0];
        Assert.AreEqual("calendar-mcp", rec.Key.Server);
        Assert.AreEqual("get_calendar_events", rec.Key.Tool);
        Assert.AreEqual("unknown", rec.Key.ErrorClass,
            "Stage A retry-failure surfaces the secondary error (permission denied) which is non-schema → unknown class");
        Assert.AreEqual("session-A", rec.SessionId);
    }

    [TestMethod]
    public async Task NoStageAProvider_NoStageB_RecordsCluster()
    {
        var store = new RecordingClusterStore();
        McpInvokeDelegate invoke = (r, _, _) => Task.FromResult(Ok(r, ""));

        var exec = new McpRecoveryExecutor(
            invoke, providers: [], NullLogger<McpRecoveryExecutor>.Instance,
            failureClusterStore: store);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "do", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'mysteryField'");

        var result = await exec.RecoverAsync("synthetic", "do", req, failed, default,
            sessionId: "session-D");

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(1, store.Records.Count);
        Assert.AreEqual("mysteryField", store.Records[0].Key.ErrorClass);
    }

    [TestMethod]
    public async Task ChainExhausted_RecordsCluster()
    {
        // Chain through MaxChainDepth (4) iterations by filling a different field
        // each retry, then surface a schema error on the final retry that triggers
        // chain-exhaustion at depth 4.
        var store = new RecordingClusterStore();
        var fields = new[] { "f1", "f2", "f3", "f4", "f5" };
        var i = 0;
        var provider = new FakeProvider(
            (_, _, _) => true,
            ctx => new ResolvedDefault($"v-{ctx.FieldName}"));

        McpInvokeDelegate invoke = (r, _, _) =>
        {
            // Each retry surfaces the next missing field, until i exhausts.
            i++;
            if (i >= fields.Length)
                return Task.FromResult(Err(r, $"Required parameter '{fields[i - 1]}'"));
            return Task.FromResult(Err(r, $"Required parameter '{fields[i]}'"));
        };

        var exec = new McpRecoveryExecutor(
            invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance,
            failureClusterStore: store);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "do", Arguments = "{}" };
        var failed = Err(req, $"Required parameter '{fields[0]}'");

        var result = await exec.RecoverAsync("synthetic", "do", req, failed, default,
            sessionId: "session-E");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "chain-exhausted");
        Assert.IsTrue(store.Records.Count >= 1, "chain exhaustion must record at least one cluster entry");
    }

    [TestMethod]
    public async Task NonSchemaError_RecordsClusterAsUnknown()
    {
        // Error string doesn't match any of Phase 1's "missing required field"
        // patterns — recovery has no field to fill, but the failure should still
        // land in the cluster store under errorClass="unknown" so DreamService
        // can spot recurring auth/network/server-side failures.
        var store = new RecordingClusterStore();
        McpInvokeDelegate invoke = (r, _, _) => Task.FromResult(Ok(r, ""));

        var exec = new McpRecoveryExecutor(
            invoke, [], NullLogger<McpRecoveryExecutor>.Instance,
            failureClusterStore: store);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get_generation", Arguments = "{}" };
        var failed = Err(req, "An error occurred invoking 'get_generation'.");

        var result = await exec.RecoverAsync("openrouter", "get_generation", req, failed, default,
            sessionId: "session-X");

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(1, store.Records.Count);
        Assert.AreEqual("unknown", store.Records[0].Key.ErrorClass);
        Assert.AreEqual("openrouter", store.Records[0].Key.Server);
        Assert.AreEqual("get_generation", store.Records[0].Key.Tool);
        Assert.AreEqual("session-X", store.Records[0].SessionId);
    }

    [TestMethod]
    public async Task FieldAlreadyInArgs_RecordsClusterAsUnknown()
    {
        // The error matches a schema pattern (extracts "timeZone") but timeZone
        // is already in the request arguments — the actual error is about
        // something else. Don't loop on recovery; record under "unknown" so
        // these "server says X is required but X is present" failures cluster
        // together rather than fragmenting by the misleading field name.
        var store = new RecordingClusterStore();
        McpInvokeDelegate invoke = (r, _, _) => Task.FromResult(Ok(r, ""));

        var exec = new McpRecoveryExecutor(
            invoke, [], NullLogger<McpRecoveryExecutor>.Instance,
            failureClusterStore: store);

        var req = new ToolInvokeRequest
        {
            ToolCallId = "1", ToolName = "get",
            Arguments = """{"timeZone":"UTC"}"""
        };
        var failed = Err(req, "Required parameter 'timeZone' not satisfied");

        var result = await exec.RecoverAsync("srv", "get", req, failed, default,
            sessionId: "session-Y");

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(1, store.Records.Count);
        Assert.AreEqual("unknown", store.Records[0].Key.ErrorClass);
    }

    [TestMethod]
    public async Task NullSessionId_StillRecords()
    {
        var store = new RecordingClusterStore();
        McpInvokeDelegate invoke = (r, _, _) => Task.FromResult(Ok(r, ""));

        var exec = new McpRecoveryExecutor(
            invoke, [], NullLogger<McpRecoveryExecutor>.Instance,
            failureClusterStore: store);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "do", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'x'");

        await exec.RecoverAsync("synthetic", "do", req, failed, default, sessionId: null);

        Assert.AreEqual(1, store.Records.Count);
        Assert.IsNull(store.Records[0].SessionId);
    }

    [TestMethod]
    public async Task NoStore_RecoveryStillWorks()
    {
        // Phase-1 contract: the executor must work without a cluster store.
        var provider = new FakeProvider(
            (_, _, f) => f == "timeZone",
            _ => new ResolvedDefault("America/Chicago"));
        McpInvokeDelegate invoke = (r, _, _) => Task.FromResult(Ok(r, "events"));

        var exec = new McpRecoveryExecutor(
            invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'timeZone'");

        var result = await exec.RecoverAsync("srv", "get", req, failed, default);

        Assert.IsFalse(result.IsError);
    }

    [TestMethod]
    public async Task StoreThrows_DoesNotBreakRecovery()
    {
        var store = new ThrowingClusterStore();
        McpInvokeDelegate invoke = (r, _, _) => Task.FromResult(Ok(r, ""));

        var exec = new McpRecoveryExecutor(
            invoke, [], NullLogger<McpRecoveryExecutor>.Instance,
            failureClusterStore: store);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "do", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'x'");

        // Must not bubble the store's exception — recovery is the priority path.
        var result = await exec.RecoverAsync("synthetic", "do", req, failed, default,
            sessionId: "session");

        Assert.IsTrue(result.IsError);
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

    private sealed record RecordedFailure(
        ClusterKey Key, string? SessionId, string ErrorMessage, DateTimeOffset At);

    private sealed class RecordingClusterStore : IFailureClusterStore
    {
        public List<RecordedFailure> Records { get; } = new();

        public Task RecordAsync(
            ClusterKey key, string? sessionId, string errorMessage, DateTimeOffset at,
            CancellationToken cancellationToken = default)
        {
            Records.Add(new RecordedFailure(key, sessionId, errorMessage, at));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FailureCluster>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FailureCluster>>([]);

        public Task<IReadOnlyList<FailureCluster>> GetEscalatableAsync(DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FailureCluster>>([]);
    }

    private sealed class ThrowingClusterStore : IFailureClusterStore
    {
        public Task RecordAsync(
            ClusterKey key, string? sessionId, string errorMessage, DateTimeOffset at,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("store offline");

        public Task<IReadOnlyList<FailureCluster>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FailureCluster>>([]);

        public Task<IReadOnlyList<FailureCluster>> GetEscalatableAsync(DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FailureCluster>>([]);
    }

    private sealed class FakeProvider(
        Func<string, string, string, bool> match,
        Func<ResolveContext, ResolvedDefault?> resolve) : IToolArgumentDefaultsProvider
    {
        public bool CanResolve(string s, string t, string f) => match(s, t, f);
        public Task<ResolvedDefault?> ResolveAsync(ResolveContext ctx, CancellationToken ct) =>
            Task.FromResult(resolve(ctx));
    }

}
