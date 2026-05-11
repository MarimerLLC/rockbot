using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests.Recovery;

[TestClass]
public class McpRecoveryExecutorTests
{
    private static ToolInvokeResponse Err(ToolInvokeRequest req, string content) => new()
    {
        ToolCallId = req.ToolCallId,
        ToolName = req.ToolName,
        Content = content,
        IsError = true
    };

    private static ToolInvokeResponse Ok(ToolInvokeRequest req, string content) => new()
    {
        ToolCallId = req.ToolCallId,
        ToolName = req.ToolName,
        Content = content,
        IsError = false
    };

    private sealed class FakeProvider(
        Func<string, string, string, bool> match,
        Func<ResolveContext, ResolvedDefault?> resolve) : IToolArgumentDefaultsProvider
    {
        public int CallCount { get; private set; }
        public bool CanResolve(string s, string t, string f) => match(s, t, f);
        public Task<ResolvedDefault?> ResolveAsync(ResolveContext ctx, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(resolve(ctx));
        }
    }

    [TestMethod]
    public async Task EmbeddedErrorInSuccessfulResponse_TriggersRecovery()
    {
        // Some MCP servers return IsError=false with {"error":"X is required"} in content.
        ToolInvokeRequest? captured = null;
        var calls = 0;
        McpInvokeDelegate invoke = (r, h, ct) =>
        {
            calls++;
            captured = r;
            return Task.FromResult(Ok(r, "events: []"));
        };

        var provider = new FakeProvider(
            (_, _, f) => f == "timeZone",
            _ => new ResolvedDefault("America/Chicago"));
        var exec = new McpRecoveryExecutor(invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        // Note: IsError = false, but the content is an embedded JSON error envelope.
        var sneakyOk = new ToolInvokeResponse
        {
            ToolCallId = req.ToolCallId,
            ToolName = req.ToolName,
            Content = "{\"error\":\"Required parameter 'timeZone' was not provided\"}",
            IsError = false
        };

        var result = await exec.RecoverAsync("srv", "get", req, sneakyOk, default);

        Assert.IsFalse(result.IsError);
        Assert.AreEqual("events: []", result.Content);
        Assert.AreEqual(1, calls, "recovery should have retried once");
        Assert.IsNotNull(captured);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(captured.Arguments!);
        Assert.AreEqual("America/Chicago", args!["timeZone"].GetString());
    }

    [TestMethod]
    public async Task EmbeddedError_ChainedRecovery_FillsMultipleFields()
    {
        // First retry surfaces a SECOND missing-field error (also IsError=false embedded).
        // Recovery should chain and fill both fields.
        var sequence = new Queue<ToolInvokeResponse>();
        var calls = new List<string>();

        McpInvokeDelegate invoke = (r, h, ct) =>
        {
            calls.Add(r.Arguments!);
            return Task.FromResult(sequence.Dequeue() with
            {
                ToolCallId = r.ToolCallId,
                ToolName = r.ToolName
            });
        };

        // Stage A retry #1 (after timeZone fill) returns embedded error about accountId.
        sequence.Enqueue(new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "x",
            Content = "{\"error\":\"accountId is required\"}",
            IsError = false
        });
        // Stage A retry #2 (after accountId fill) returns success.
        sequence.Enqueue(new ToolInvokeResponse
        {
            ToolCallId = "x", ToolName = "x",
            Content = "[]",
            IsError = false
        });

        var tzProvider = new FakeProvider(
            (_, _, f) => f == "timeZone",
            _ => new ResolvedDefault("UTC"));
        var idProvider = new FakeProvider(
            (_, _, f) => f == "accountId",
            _ => new ResolvedDefault("a@x.com"));

        var exec = new McpRecoveryExecutor(
            invoke, [tzProvider, idProvider], NullLogger<McpRecoveryExecutor>.Instance);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get_events", Arguments = "{}" };
        var initial = Err(req, "Required parameter 'timeZone' was not provided");

        var result = await exec.RecoverAsync("calendar-mcp", "get_events", req, initial, default);

        Assert.IsFalse(result.IsError);
        Assert.AreEqual("[]", result.Content);
        Assert.AreEqual(2, calls.Count, "should retry twice — once per chained field");

        // First retry should have just timeZone.
        var args1 = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(calls[0]);
        Assert.AreEqual("UTC", args1!["timeZone"].GetString());
        Assert.IsFalse(args1.ContainsKey("accountId"));

        // Second retry should carry both timeZone and accountId.
        var args2 = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(calls[1]);
        Assert.AreEqual("UTC", args2!["timeZone"].GetString());
        Assert.AreEqual("a@x.com", args2["accountId"].GetString());
    }

    [TestMethod]
    public async Task ChainedRecovery_BoundedDepth_StopsAtMax()
    {
        // Every retry surfaces a new "Required parameter 'fN'" error. Recovery should
        // give up at MaxChainDepth without infinitely looping.
        var n = 0;
        var calls = 0;
        McpInvokeDelegate invoke = (r, h, ct) =>
        {
            calls++;
            n++;
            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = r.ToolCallId,
                ToolName = r.ToolName,
                Content = $"{{\"error\":\"Required parameter 'f{n}'\"}}",
                IsError = false
            });
        };

        // A provider that resolves *anything* — so chain only ends when depth runs out.
        var provider = new FakeProvider((_, _, _) => true, _ => new ResolvedDefault("v"));
        var exec = new McpRecoveryExecutor(invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        var initial = Err(req, "Required parameter 'f0'");

        var result = await exec.RecoverAsync("srv", "get", req, initial, default);

        Assert.IsTrue(result.IsError);
        // Each chain iteration issues exactly one retry call. With MaxChainDepth=4 we
        // get at most 4 retries before giving up.
        Assert.IsTrue(calls <= McpRecoveryExecutor.MaxChainDepth,
            $"expected ≤ {McpRecoveryExecutor.MaxChainDepth} calls, got {calls}");
    }

    [TestMethod]
    public async Task SuccessfulResponse_NoEmbeddedError_PassesThrough()
    {
        var calls = 0;
        McpInvokeDelegate invoke = (r, h, ct) => { calls++; return Task.FromResult(Ok(r, "events")); };
        var exec = new McpRecoveryExecutor(invoke, [], NullLogger<McpRecoveryExecutor>.Instance);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        var success = Ok(req, """{"events":[],"count":0}""");

        var result = await exec.RecoverAsync("srv", "get", req, success, default);

        Assert.IsFalse(result.IsError);
        Assert.AreEqual("""{"events":[],"count":0}""", result.Content);
        Assert.AreEqual(0, calls, "successful responses should not be re-invoked");
    }

    [TestMethod]
    public async Task EmbeddedError_NotMatchingPattern_PassesThrough()
    {
        // Embedded error exists but doesn't match any "missing field" pattern.
        var calls = 0;
        McpInvokeDelegate invoke = (r, h, ct) => { calls++; return Task.FromResult(Ok(r, "")); };
        var exec = new McpRecoveryExecutor(invoke, [], NullLogger<McpRecoveryExecutor>.Instance);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        var rateLimit = new ToolInvokeResponse
        {
            ToolCallId = req.ToolCallId,
            ToolName = req.ToolName,
            Content = """{"error":"rate limit exceeded"}""",
            IsError = false
        };

        var result = await exec.RecoverAsync("srv", "get", req, rateLimit, default);

        Assert.IsFalse(result.IsError);
        Assert.AreEqual("""{"error":"rate limit exceeded"}""", result.Content);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void TryExtractRecoverableError_VariousShapes()
    {
        // IsError=true: returns content.
        var asError = new ToolInvokeResponse { ToolCallId = "x", ToolName = "y", Content = "boom", IsError = true };
        Assert.AreEqual("boom", McpRecoveryExecutor.TryExtractRecoverableError(asError));

        // IsError=false plain text: returns null.
        var asText = new ToolInvokeResponse { ToolCallId = "x", ToolName = "y", Content = "results", IsError = false };
        Assert.IsNull(McpRecoveryExecutor.TryExtractRecoverableError(asText));

        // IsError=false JSON with "error" string: returns the error message.
        var asJsonErr = new ToolInvokeResponse { ToolCallId = "x", ToolName = "y", Content = """{"error":"X is required"}""", IsError = false };
        Assert.AreEqual("X is required", McpRecoveryExecutor.TryExtractRecoverableError(asJsonErr));

        // IsError=false JSON with "Error" capitalised: also recognised.
        var asJsonErrCap = new ToolInvokeResponse { ToolCallId = "x", ToolName = "y", Content = """{"Error":"X is required"}""", IsError = false };
        Assert.AreEqual("X is required", McpRecoveryExecutor.TryExtractRecoverableError(asJsonErrCap));

        // IsError=false JSON without "error" property: null.
        var asJsonOk = new ToolInvokeResponse { ToolCallId = "x", ToolName = "y", Content = """{"data":[]}""", IsError = false };
        Assert.IsNull(McpRecoveryExecutor.TryExtractRecoverableError(asJsonOk));

        // IsError=false with non-JSON content starting with '{': null (parse fails).
        var asMalformed = new ToolInvokeResponse { ToolCallId = "x", ToolName = "y", Content = "{not json", IsError = false };
        Assert.IsNull(McpRecoveryExecutor.TryExtractRecoverableError(asMalformed));

        // Empty content: null.
        var asEmpty = new ToolInvokeResponse { ToolCallId = "x", ToolName = "y", Content = "", IsError = false };
        Assert.IsNull(McpRecoveryExecutor.TryExtractRecoverableError(asEmpty));

        // Large content: skipped.
        var big = new string('a', McpRecoveryExecutor.MaxEmbeddedErrorScanLength + 1);
        var asBig = new ToolInvokeResponse { ToolCallId = "x", ToolName = "y", Content = $"{{\"error\":\"{big}\"}}", IsError = false };
        Assert.IsNull(McpRecoveryExecutor.TryExtractRecoverableError(asBig));

        // "message" field is NOT treated as an error — too many false positives.
        var asMessage = new ToolInvokeResponse { ToolCallId = "x", ToolName = "y", Content = """{"message":"Required parameter 'x'"}""", IsError = false };
        Assert.IsNull(McpRecoveryExecutor.TryExtractRecoverableError(asMessage));
    }

    [TestMethod]
    public async Task NoErrorMatch_ReturnsOriginalUnchanged()
    {
        var calls = new List<(ToolInvokeRequest, IReadOnlyDictionary<string, string>?)>();
        McpInvokeDelegate invoke = (r, h, ct) =>
        {
            calls.Add((r, h));
            return Task.FromResult(Ok(r, "should not retry"));
        };
        var exec = new McpRecoveryExecutor(invoke, [], NullLogger<McpRecoveryExecutor>.Instance);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        var failed = Err(req, "connection refused");

        var result = await exec.RecoverAsync("srv", "get", req, failed, default);

        Assert.IsTrue(result.IsError);
        Assert.AreEqual("connection refused", result.Content);
        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public async Task StageA_FieldAlreadyPresent_DoesNotLoop()
    {
        var calls = 0;
        McpInvokeDelegate invoke = (r, h, ct) => { calls++; return Task.FromResult(Ok(r, "{}")); };
        var provider = new FakeProvider((_, _, _) => true, _ => new ResolvedDefault("UTC"));
        var exec = new McpRecoveryExecutor(invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{\"timeZone\":\"UTC\"}" };
        var failed = Err(req, "Required parameter 'timeZone'");

        var result = await exec.RecoverAsync("srv", "get", req, failed, default);

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(0, calls, "should not retry when field is already in args");
        Assert.AreEqual(0, provider.CallCount, "should not call provider when field already present");
    }

    [TestMethod]
    public async Task StageA_ProviderResolves_RetryMergesAndSucceeds()
    {
        ToolInvokeRequest? captured = null;
        IReadOnlyDictionary<string, string>? capturedHeaders = null;
        McpInvokeDelegate invoke = (r, h, ct) =>
        {
            captured = r;
            capturedHeaders = h;
            return Task.FromResult(Ok(r, "events: []"));
        };

        var provider = new FakeProvider(
            (s, _, f) => s == "calendar-mcp" && f == "timeZone",
            _ => new ResolvedDefault("America/Chicago"));

        var exec = new McpRecoveryExecutor(invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get_events", Arguments = "{\"start\":\"2026-01-01\"}" };
        var failed = Err(req, "Required parameter 'timeZone'");

        var result = await exec.RecoverAsync("calendar-mcp", "get_events", req, failed, default);

        Assert.IsFalse(result.IsError);
        Assert.AreEqual("events: []", result.Content);
        Assert.IsNotNull(captured);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(captured.Arguments!);
        Assert.IsNotNull(args);
        Assert.IsTrue(args.ContainsKey("start"), "existing args preserved");
        Assert.IsTrue(args.ContainsKey("timeZone"));
        Assert.AreEqual("America/Chicago", args["timeZone"].GetString());
        Assert.IsNotNull(capturedHeaders);
        Assert.AreEqual("calendar-mcp", capturedHeaders[McpHeaders.ServerName]);
    }

    [TestMethod]
    public async Task StageA_RetryFailsAgain_AnnotatesTrail()
    {
        McpInvokeDelegate invoke = (r, h, ct) => Task.FromResult(Err(r, "still broken somehow"));
        var provider = new FakeProvider((_, _, _) => true, _ => new ResolvedDefault("v"));

        var exec = new McpRecoveryExecutor(invoke, [provider], NullLogger<McpRecoveryExecutor>.Instance);
        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'x'");

        var result = await exec.RecoverAsync("srv", "get", req, failed, default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "[recovery-trail]");
        StringAssert.Contains(result.Content, "stageA=FakeProvider");
        StringAssert.Contains(result.Content, "still broken somehow");
        // Original error preserved at the head of the message.
        StringAssert.StartsWith(result.Content, "Required parameter 'x'");
    }

    [TestMethod]
    public async Task StageA_NoProvider_NoStageB_AnnotatesNoProvider()
    {
        McpInvokeDelegate invoke = (r, h, ct) => Task.FromResult(Ok(r, ""));
        var exec = new McpRecoveryExecutor(invoke, [], NullLogger<McpRecoveryExecutor>.Instance);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'novel_field'");

        var result = await exec.RecoverAsync("srv", "get", req, failed, default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "stageA=no-provider");
        StringAssert.Contains(result.Content, "stageB=disabled");
    }

    [TestMethod]
    public async Task NoProvider_WithEnricher_ReturnsEnrichedErrorAndSkipsStageB()
    {
        // Amendment 1: when no environmental provider can fill the missing field
        // and the SchemaErrorEnricher is wired, recovery surfaces a schema-rich
        // error to the LLM instead of guessing (or recording a capability claim).
        var claimWriter = new RecordingClaimWriter();
        var stageBCalled = false;
        var stageB = new ProbeStageBLlmFiller(() => stageBCalled = true);

        var schemasByServer = new Dictionary<string, IReadOnlyList<McpToolDefinition>>
        {
            ["calendar-mcp"] = new[]
            {
                new McpToolDefinition
                {
                    Name = "get_email_details",
                    Description = "Returns email content. Both accountId and emailId are required.",
                    ParametersSchema = """{"type":"object","properties":{"emailId":{"type":"string","description":"Email id from search_emails"}},"required":["emailId"]}"""
                }
            }
        };
        var cache = new ToolSchemaCache((server, _) =>
            schemasByServer.TryGetValue(server, out var t)
                ? Task.FromResult<IReadOnlyList<McpToolDefinition>?>(t)
                : Task.FromResult<IReadOnlyList<McpToolDefinition>?>(null));
        var enricher = new SchemaErrorEnricher(cache, new EmptyToolCallLog());

        McpInvokeDelegate invoke = (r, _, _) => Task.FromResult(Ok(r, ""));
        var exec = new McpRecoveryExecutor(
            invoke, providers: [],
            NullLogger<McpRecoveryExecutor>.Instance,
            stageB: stageB,
            capabilityClaimWriter: claimWriter,
            enricher: enricher);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get_email_details", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'emailId' was not provided");

        var result = await exec.RecoverAsync("calendar-mcp", "get_email_details", req, failed, default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "[mcp-recovery]");
        StringAssert.Contains(result.Content, "missing required field 'emailId'");
        StringAssert.Contains(result.Content, "Field schema:");
        StringAssert.Contains(result.Content, "Email id from search_emails");

        Assert.IsFalse(stageBCalled, "enricher must short-circuit Stage B");
        Assert.AreEqual(0, claimWriter.Saved.Count,
            "enrichment teaches the LLM directly — no capability claim is written");
    }

    private sealed class ProbeStageBLlmFiller(Action onCall)
        : StageBLlmFiller(new NoopLlmClientForProbe(), NullLogger<StageBLlmFiller>.Instance)
    {
        public override Task<object?> TryFillAsync(
            string serverName, string toolName, string fieldName,
            IReadOnlyDictionary<string, object?> existingArgs,
            string? originalErrorText, CancellationToken ct)
        {
            onCall();
            return Task.FromResult<object?>(null);
        }
    }

    private sealed class NoopLlmClientForProbe : RockBot.Host.ILlmClient
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

    private sealed class RecordingClaimWriter : RockBot.Host.ICapabilityClaimWriter
    {
        public List<RockBot.Host.CapabilityClaim> Saved { get; } = new();
        public Task SaveCapabilityClaimAsync(RockBot.Host.CapabilityClaim claim, CancellationToken ct = default)
        {
            Saved.Add(claim);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyToolCallLog : RockBot.Host.IToolCallLog
    {
        public Task AppendAsync(RockBot.Host.ToolCallEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RockBot.Host.ToolCallEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RockBot.Host.ToolCallEvent>>([]);
        public Task<IReadOnlyList<RockBot.Host.ToolCallEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RockBot.Host.ToolCallEvent>>([]);
    }

    [TestMethod]
    public async Task ProviderOrder_FirstMatchWins()
    {
        var pA = new FakeProvider((_, _, f) => f == "x", _ => new ResolvedDefault("from-A"));
        var pB = new FakeProvider((_, _, f) => f == "x", _ => new ResolvedDefault("from-B"));

        ToolInvokeRequest? captured = null;
        McpInvokeDelegate invoke = (r, h, ct) => { captured = r; return Task.FromResult(Ok(r, "ok")); };
        var exec = new McpRecoveryExecutor(invoke, [pA, pB], NullLogger<McpRecoveryExecutor>.Instance);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'x'");

        var result = await exec.RecoverAsync("srv", "get", req, failed, default);

        Assert.IsFalse(result.IsError);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(captured!.Arguments!);
        Assert.AreEqual("from-A", args!["x"].GetString());
        Assert.AreEqual(1, pA.CallCount);
        Assert.AreEqual(0, pB.CallCount);
    }

    [TestMethod]
    public async Task ProviderResolves_RetrySurfacesDifferentMissingField_ChainsAndFills()
    {
        // Regression for Amendment 1: previously, fan-out responses returned mergedArgs=null
        // and blocked chained recovery. With fan-out removed, a Stage A resolve that surfaces
        // a *different* missing field on retry must still chain to fill it on the next pass.
        var calls = new List<string>();
        var n = 0;
        McpInvokeDelegate invoke = (r, h, ct) =>
        {
            calls.Add(r.Arguments!);
            n++;
            // First retry (accountId filled) surfaces the next missing field.
            if (n == 1)
                return Task.FromResult(Err(r, "Required parameter 'emailId' was not provided"));
            // Second retry (emailId filled too) succeeds.
            return Task.FromResult(Ok(r, "email body"));
        };

        var idProvider = new FakeProvider(
            (_, _, f) => f == "accountId",
            _ => new ResolvedDefault("a@x.com"));
        var emailProvider = new FakeProvider(
            (_, _, f) => f == "emailId",
            _ => new ResolvedDefault("EMAIL-1"));

        var exec = new McpRecoveryExecutor(
            invoke, [idProvider, emailProvider], NullLogger<McpRecoveryExecutor>.Instance);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "get_email_details", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'accountId'");

        var result = await exec.RecoverAsync("calendar-mcp", "get_email_details", req, failed, default);

        Assert.IsFalse(result.IsError, "chained recovery should succeed for sequential missing fields");
        Assert.AreEqual("email body", result.Content);
        Assert.AreEqual(2, calls.Count, "two retries: one per field");

        var finalArgs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(calls[1]);
        Assert.AreEqual("a@x.com", finalArgs!["accountId"].GetString());
        Assert.AreEqual("EMAIL-1", finalArgs["emailId"].GetString());
    }

    [TestMethod]
    public async Task StageB_FillsNovelField_RetrySucceeds()
    {
        ToolInvokeRequest? captured = null;
        McpInvokeDelegate invoke = (r, h, ct) => { captured = r; return Task.FromResult(Ok(r, "ok")); };

        var stageB = new TestStageBLlmFiller("\"recovered-value\"");
        var exec = new McpRecoveryExecutor(invoke, [], NullLogger<McpRecoveryExecutor>.Instance, stageB.Filler);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "do", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'quux'");

        var result = await exec.RecoverAsync("synthetic", "do", req, failed, default);

        Assert.IsFalse(result.IsError);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(captured!.Arguments!);
        Assert.AreEqual("recovered-value", args!["quux"].GetString());
    }

    [TestMethod]
    public async Task StageB_FillFails_AnnotatesTrail()
    {
        McpInvokeDelegate invoke = (r, h, ct) => Task.FromResult(Ok(r, "ok"));
        var stageB = new TestStageBLlmFiller(null); // returns null → fill failed
        var exec = new McpRecoveryExecutor(invoke, [], NullLogger<McpRecoveryExecutor>.Instance, stageB.Filler);

        var req = new ToolInvokeRequest { ToolCallId = "1", ToolName = "do", Arguments = "{}" };
        var failed = Err(req, "Required parameter 'quux'");

        var result = await exec.RecoverAsync("synthetic", "do", req, failed, default);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "stageB=fill-failed");
    }

    /// <summary>
    /// Wraps StageBLlmFiller with a stub <see cref="Microsoft.Extensions.AI.IChatClient"/>-free
    /// path. We cannot easily stub <see cref="RockBot.Host.ILlmClient"/> here, so this helper
    /// builds a derived filler whose <c>TryFillAsync</c> returns the canned JSON.
    /// </summary>
    private sealed class TestStageBLlmFiller
    {
        public StageBLlmFiller Filler { get; }
        public TestStageBLlmFiller(string? cannedJson)
        {
            Filler = new TestFiller(cannedJson);
        }

        private sealed class TestFiller(string? cannedJson)
            : StageBLlmFiller(new NoopLlmClient(), NullLogger<StageBLlmFiller>.Instance)
        {
            public override Task<object?> TryFillAsync(
                string serverName, string toolName, string fieldName,
                IReadOnlyDictionary<string, object?> existingArgs,
                string? originalErrorText, CancellationToken ct)
            {
                if (cannedJson is null) return Task.FromResult<object?>(null);
                var doc = JsonDocument.Parse(cannedJson);
                return Task.FromResult<object?>(McpToolExecutor.ConvertJsonElement(doc.RootElement));
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
}
