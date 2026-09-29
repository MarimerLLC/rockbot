using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Llm.Tests;

[TestClass]
public class FallbackChatClientTests
{
    // Zero delay + zero max-retries for the retry test (1 retry = attempt 0 then attempt 1)
    private static FallbackChatClient Build(
        IReadOnlyList<(string ModelId, IChatClient Client)> entries,
        int maxRetries = 1,
        TimeSpan? cooldownPeriod = null) =>
        new(entries, NullLogger.Instance, retryDelay: TimeSpan.Zero, maxRetries: maxRetries,
            cooldownPeriod: cooldownPeriod);

    private static ChatResponse OkResponse(string text = "ok") =>
        new(new ChatMessage(ChatRole.Assistant, text));

    private static HttpRequestException HttpEx(HttpStatusCode status) =>
        new("err", null, status);

    /// <summary>
    /// Constructs a <see cref="ClientResultException"/> with the given HTTP status,
    /// matching what the OpenAI SDK throws on a failed request.
    /// </summary>
    private static ClientResultException ClientResultEx(int status) =>
        new("Service request failed.", new StatusOnlyPipelineResponse(status));

    /// <summary>
    /// Constructs the exact shape Azure OpenAI surfaces when its content filter trips:
    /// a <see cref="ClientResultException"/> with status 400 whose message includes
    /// the literal token <c>content_filter</c>.
    /// </summary>
    private static ClientResultException AzureContentFilterEx() =>
        new("HTTP 400 (: content_filter)\nParameter: prompt\n\nThe response was filtered " +
            "due to the prompt triggering Azure OpenAI's content management policy.",
            new StatusOnlyPipelineResponse(400));

    // ── test stubs ───────────────────────────────────────────────────────────

    /// <summary>Always returns the same response or throws the same exception.</summary>
    private sealed class FixedStub(ChatResponse? response = null, Exception? ex = null) : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (ex is not null) return Task.FromException<ChatResponse>(ex);
            return Task.FromResult(response ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Returns results from a queue: dequeues each call in order.</summary>
    private sealed class SequentialStub : IChatClient
    {
        private readonly Queue<(ChatResponse? Response, Exception? Ex)> _queue = new();
        public int CallCount { get; private set; }

        public void Enqueue(ChatResponse response) => _queue.Enqueue((response, null));
        public void Enqueue(Exception ex) => _queue.Enqueue((null, ex));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_queue.TryDequeue(out var entry))
            {
                if (entry.Ex is not null) return Task.FromException<ChatResponse>(entry.Ex);
                return Task.FromResult(entry.Response!);
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Returns a known value from GetService for a specific type.</summary>
    private sealed class ServiceStub(Type serviceType, object? service) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public object? GetService(Type t, object? key = null) => t == serviceType ? service : null;
        public void Dispose() { }
    }

    // ── tests ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SwitchesToNextModel_OnQuotaError()
    {
        var first  = new FixedStub(ex: HttpEx(HttpStatusCode.PaymentRequired));
        var second = new FixedStub(response: OkResponse("from-second"));

        var client = Build([("m1", first), ("m2", second)], maxRetries: 0);

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("from-second", response.Messages[^1].Text);
        Assert.AreEqual(1, first.CallCount);
        Assert.AreEqual(1, second.CallCount);
    }

    [TestMethod]
    public async Task SwitchesToNextModel_OnHardError()
    {
        var first  = new FixedStub(ex: HttpEx(HttpStatusCode.Unauthorized));
        var second = new FixedStub(response: OkResponse("from-second"));

        var client = Build([("m1", first), ("m2", second)], maxRetries: 0);

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("from-second", response.Messages[^1].Text);
        Assert.AreEqual(1, first.CallCount);
        Assert.AreEqual(1, second.CallCount);
    }

    [TestMethod]
    public async Task RetriesSameModel_OnTransientError()
    {
        var stub = new SequentialStub();
        stub.Enqueue(HttpEx(HttpStatusCode.ServiceUnavailable)); // attempt 0: 503
        stub.Enqueue(OkResponse("retried"));                  // attempt 1: success

        var client = Build([("m1", stub)], maxRetries: 1);

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("retried", response.Messages[^1].Text);
        Assert.AreEqual(2, stub.CallCount);
    }

    [TestMethod]
    public async Task PermanentDegradation_SkipsExhaustedModelOnSubsequentCalls()
    {
        var first  = new FixedStub(ex: HttpEx(HttpStatusCode.PaymentRequired));
        var second = new FixedStub(response: OkResponse());

        var client = Build([("m1", first), ("m2", second)], maxRetries: 0);

        // First call: first model fails, second succeeds; first is now degraded
        await client.GetResponseAsync([]);

        // Second call: should go directly to second (first stays degraded)
        await client.GetResponseAsync([]);

        Assert.AreEqual(1, first.CallCount);   // only called once, then permanently skipped
        Assert.AreEqual(2, second.CallCount);
    }

    [TestMethod]
    public async Task AllModelsExhausted_ThrowsException()
    {
        var first  = new FixedStub(ex: HttpEx(HttpStatusCode.Unauthorized));
        var second = new FixedStub(ex: HttpEx(HttpStatusCode.PaymentRequired));

        var client = Build([("m1", first), ("m2", second)], maxRetries: 0);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.GetResponseAsync([]));
    }

    [TestMethod]
    public async Task CooldownRecovery_RestoresDegradedModelAfterElapsed()
    {
        var first  = new SequentialStub();
        first.Enqueue(HttpEx(HttpStatusCode.PaymentRequired)); // call 1: degrade
        first.Enqueue(OkResponse("recovered"));                 // call 3: after cooldown

        var second = new FixedStub(response: OkResponse("from-second"));

        // Use a tiny cooldown so the test doesn't block
        var client = Build([("m1", first), ("m2", second)], maxRetries: 0,
            cooldownPeriod: TimeSpan.FromMilliseconds(50));

        // Call 1: first fails → falls back to second
        var r1 = await client.GetResponseAsync([]);
        Assert.AreEqual("from-second", r1.Messages[^1].Text);

        // Call 2: first is still degraded (cooldown not elapsed) → second
        var r2 = await client.GetResponseAsync([]);
        Assert.AreEqual("from-second", r2.Messages[^1].Text);

        // Wait for cooldown to elapse
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Call 3: first should be recovered and retried
        var r3 = await client.GetResponseAsync([]);
        Assert.AreEqual("recovered", r3.Messages[^1].Text);
        Assert.AreEqual(2, first.CallCount);  // degraded call + recovered call
    }

    [TestMethod]
    public async Task CooldownRecovery_RepeatsAfterRedegradation()
    {
        var first  = new SequentialStub();
        first.Enqueue(HttpEx(HttpStatusCode.PaymentRequired)); // call 1: degrade
        first.Enqueue(HttpEx(HttpStatusCode.PaymentRequired)); // call 3: still down after first cooldown
        first.Enqueue(OkResponse("finally"));                   // call 5: recovered after second cooldown

        var second = new FixedStub(response: OkResponse("fallback"));

        var client = Build([("m1", first), ("m2", second)], maxRetries: 0,
            cooldownPeriod: TimeSpan.FromMilliseconds(50));

        // Call 1: first fails → fallback
        await client.GetResponseAsync([]);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Call 2: cooldown elapsed, retry primary → still down → re-degraded → fallback
        var r2 = await client.GetResponseAsync([]);
        Assert.AreEqual("fallback", r2.Messages[^1].Text);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Call 3: second cooldown elapsed, retry primary → now up
        var r3 = await client.GetResponseAsync([]);
        Assert.AreEqual("finally", r3.Messages[^1].Text);
    }

    [TestMethod]
    public async Task ContentFilter_FallsBackWithoutDegrading()
    {
        var first  = new FixedStub(ex: new Exception("HTTP 400 (: content_filter)"));
        var second = new FixedStub(response: OkResponse("from-second"));

        var client = Build([("azure-model", first), ("openrouter-model", second)], maxRetries: 0);

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("from-second", response.Messages[^1].Text);
        Assert.AreEqual(1, first.CallCount);
        Assert.AreEqual(1, second.CallCount);
    }

    [TestMethod]
    public async Task ContentFilter_DoesNotDegradeModel_SubsequentCallsRetryPrimary()
    {
        var first = new SequentialStub();
        first.Enqueue(new Exception("HTTP 400 (: content_filter)")); // call 1: filtered
        first.Enqueue(OkResponse("azure-ok"));                       // call 2: normal request works

        var second = new FixedStub(response: OkResponse("fallback"));

        var client = Build([("azure-model", first), ("openrouter-model", second)], maxRetries: 0);

        // Call 1: content filter → falls back to second
        var r1 = await client.GetResponseAsync([]);
        Assert.AreEqual("fallback", r1.Messages[^1].Text);

        // Call 2: first model should be tried again (not degraded)
        var r2 = await client.GetResponseAsync([]);
        Assert.AreEqual("azure-ok", r2.Messages[^1].Text);
        Assert.AreEqual(2, first.CallCount);
    }

    [TestMethod]
    public async Task ContentFilter_AllModelsFiltered_Throws()
    {
        var first  = new FixedStub(ex: new Exception("HTTP 400 (: content_filter)"));
        var second = new FixedStub(ex: new Exception("HTTP 400 (: content_filter)"));

        var client = Build([("m1", first), ("m2", second)], maxRetries: 0);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.GetResponseAsync([]));
    }

    [TestMethod]
    public async Task ContentFilter_AzureClientResultException_FallsBackToNextModel()
    {
        // Regression: Azure surfaces content filter rejections as ClientResultException
        // with Status=400. Status-code-only classification mapped 400 to Unknown and
        // re-threw immediately, so the OpenRouter fallbacks configured for the Balanced
        // tier were never reached. Verify the real exception shape now falls through.
        var first  = new FixedStub(ex: AzureContentFilterEx());
        var second = new FixedStub(response: OkResponse("from-openrouter"));

        var client = Build([("azure-gpt", first), ("openrouter-gemini", second)], maxRetries: 0);

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("from-openrouter", response.Messages[^1].Text);
        Assert.AreEqual(1, first.CallCount);
        Assert.AreEqual(1, second.CallCount);
    }

    [TestMethod]
    public async Task PerAttemptTimeout_FallsBackToNextModel()
    {
        // First model stalls forever; second responds immediately.
        var first  = new SlowStub(delay: TimeSpan.FromSeconds(30));
        var second = new FixedStub(response: OkResponse("from-fallback"));

        var client = new FallbackChatClient(
            [("slow-model", first), ("fast-model", second)],
            NullLogger.Instance,
            retryDelay: TimeSpan.Zero,
            maxRetries: 0,
            perAttemptTimeout: TimeSpan.FromMilliseconds(100));

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("from-fallback", response.Messages[^1].Text);
        Assert.AreEqual(1, first.CallCount, "Slow model should have been called once");
        Assert.AreEqual(1, second.CallCount, "Fast model should have been called as fallback");
    }

    [TestMethod]
    public async Task ClientResultException_503_RetriesAndFallsBack()
    {
        // Regression: a 503 from the OpenAI SDK surfaces as ClientResultException, not
        // HttpRequestException. Before the fix it was classified Unknown and re-thrown
        // immediately, bypassing both retry and the fallback chain.
        var first  = new SequentialStub();
        first.Enqueue(ClientResultEx(503)); // attempt 0: 503
        first.Enqueue(ClientResultEx(503)); // attempt 1 (retry): still 503
        var second = new FixedStub(response: OkResponse("from-second"));

        var client = Build([("m1", first), ("m2", second)], maxRetries: 1);

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("from-second", response.Messages[^1].Text);
        Assert.AreEqual(2, first.CallCount, "503 should be retried once on the same model");
        Assert.AreEqual(1, second.CallCount);
    }

    [TestMethod]
    public async Task ClientResultException_TransientStatuses_FallBack()
    {
        foreach (var status in new[] { 408, 429, 500, 502, 503, 504 })
        {
            var first  = new FixedStub(ex: ClientResultEx(status));
            var second = new FixedStub(response: OkResponse($"fallback-{status}"));
            var client = Build([("m1", first), ("m2", second)], maxRetries: 0);

            var response = await client.GetResponseAsync([]);

            Assert.AreEqual($"fallback-{status}", response.Messages[^1].Text,
                $"Status {status} should fall back to the next model");
        }
    }

    [TestMethod]
    public async Task ClientResultException_HardErrorStatus_FallsBackAndDegrades()
    {
        var first  = new FixedStub(ex: ClientResultEx(401));
        var second = new FixedStub(response: OkResponse());
        var client = Build([("m1", first), ("m2", second)], maxRetries: 0);

        await client.GetResponseAsync([]);
        await client.GetResponseAsync([]);

        Assert.AreEqual(1, first.CallCount, "401 should mark model degraded; second call must skip it");
        Assert.AreEqual(2, second.CallCount);
    }

    [TestMethod]
    public async Task ClientResultException_Status0_TreatsAsTransient_AndFallsBack()
    {
        // Regression: when the OpenAI/Azure SDK exhausts its own internal retry policy
        // on a transport-level failure ("Retry failed after 4 tries. (Resource
        // temporarily unavailable …)"), it surfaces as ClientResultException with
        // Status=0 because no HTTP response was ever received. Status-code lookup
        // mapped 0 to Unknown and re-threw, so OpenRouter never got a chance.
        var first  = new FixedStub(ex: ClientResultEx(0));
        var second = new FixedStub(response: OkResponse("from-openrouter"));

        var client = Build([("azure-balanced", first), ("openrouter-balanced", second)], maxRetries: 0);

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("from-openrouter", response.Messages[^1].Text);
        Assert.AreEqual(1, first.CallCount);
        Assert.AreEqual(1, second.CallCount);
    }

    [TestMethod]
    public async Task HttpRequestException_NullStatus_TreatsAsTransient_AndFallsBack()
    {
        // Regression: HttpRequestException with no StatusCode means the request
        // failed before any response was received (DNS failure, TCP reset). The
        // previous classifier required a non-null StatusCode and fell through to
        // Unknown.
        var first  = new FixedStub(ex: new HttpRequestException("Connection refused"));
        var second = new FixedStub(response: OkResponse("from-second"));

        var client = Build([("m1", first), ("m2", second)], maxRetries: 0);

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("from-second", response.Messages[^1].Text);
    }

    [TestMethod]
    public async Task SocketException_WrappedInInnerException_FallsBack()
    {
        var socketEx = new System.Net.Sockets.SocketException();
        var wrapped  = new Exception("Pipeline failure", socketEx);
        var first    = new FixedStub(ex: wrapped);
        var second   = new FixedStub(response: OkResponse("from-second"));

        var client = Build([("m1", first), ("m2", second)], maxRetries: 0);

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("from-second", response.Messages[^1].Text);
    }

    [TestMethod]
    public async Task RetryFailedAfterMessage_FallsBack()
    {
        // The exact shape the user saw: a plain exception whose message is the
        // SDK retry-exhaustion wrapper. No HTTP status, no recognizable inner.
        var first  = new FixedStub(ex: new Exception(
            "Retry failed after 4 tries. " +
            "(Resource temporarily unavailable (rocky-ml1nznjr-eastus2.cognitiveservices.azure.com:443))"));
        var second = new FixedStub(response: OkResponse("from-openrouter"));

        var client = Build([("azure-balanced", first), ("openrouter-balanced", second)], maxRetries: 0);

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("from-openrouter", response.Messages[^1].Text);
    }

    [TestMethod]
    public async Task PerAttemptTimeout_UserCancellation_PropagatesImmediately()
    {
        var slow = new SlowStub(delay: TimeSpan.FromSeconds(30));
        var fallback = new FixedStub(response: OkResponse("should-not-reach"));

        var client = new FallbackChatClient(
            [("slow", slow), ("fallback", fallback)],
            NullLogger.Instance,
            retryDelay: TimeSpan.Zero,
            maxRetries: 0,
            perAttemptTimeout: TimeSpan.FromSeconds(5));

        using var userCts = new CancellationTokenSource();
        userCts.Cancel(); // User cancelled immediately

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.GetResponseAsync([], cancellationToken: userCts.Token));

        Assert.AreEqual(0, fallback.CallCount, "Should not fall back on user cancellation");
    }

    // ── additional stubs ────────────────────────────────────────────────────

    /// <summary>
    /// Minimal PipelineResponse that only carries an HTTP status — enough to construct
    /// a ClientResultException whose Status property the classifier can read.
    /// </summary>
    private sealed class StatusOnlyPipelineResponse(int status) : PipelineResponse
    {
        public override int Status { get; } = status;
        public override string ReasonPhrase => string.Empty;
        public override Stream? ContentStream { get => null; set { } }
        public override BinaryData Content => BinaryData.Empty;
        protected override PipelineResponseHeaders HeadersCore => throw new NotImplementedException();
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            new(BinaryData.Empty);
        public override void Dispose() { }
    }

    /// <summary>Delays for a configurable period before responding (simulates a stalled model).</summary>
    private sealed class SlowStub(TimeSpan delay) : IChatClient
    {
        public int CallCount { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Delay(delay, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "slow-response"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // ── rate limiting (429) ──────────────────────────────────────────────────

    /// <summary>A 429 as Azure OpenAI sends it, with the given response headers.</summary>
    private static ClientResultException RateLimitEx(params (string Name, string Value)[] headers) =>
        new("HTTP 429 (Too Many Requests)", new HeaderedPipelineResponse(429, headers));

    private sealed class HeaderedPipelineResponse(int status, (string Name, string Value)[] headers)
        : PipelineResponse
    {
        public override int Status { get; } = status;
        public override string ReasonPhrase => string.Empty;
        public override Stream? ContentStream { get => null; set { } }
        public override BinaryData Content => BinaryData.Empty;
        protected override PipelineResponseHeaders HeadersCore { get; } = new HeaderSet(headers);
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            new(BinaryData.Empty);
        public override void Dispose() { }

        private sealed class HeaderSet((string Name, string Value)[] headers) : PipelineResponseHeaders
        {
            public override bool TryGetValue(string name, out string? value)
            {
                value = headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
                return value is not null;
            }

            public override bool TryGetValues(string name, out IEnumerable<string>? values)
            {
                values = TryGetValue(name, out var v) ? [v!] : null;
                return values is not null;
            }

            public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
                headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).GetEnumerator();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [TestMethod]
    public async Task RateLimited_FallsBackImmediately_WithoutRetryingSameModel()
    {
        // Regression: a 429 used to be retried on the same model. Behind the SDK's
        // retry-after handling each attempt ran to the 90s per-attempt timeout, so a
        // throttled turn took ~3 minutes to reach the fallback and subagents timed out.
        var first  = new FixedStub(ex: RateLimitEx(("retry-after", "30")));
        var second = new FixedStub(response: OkResponse("from-second"));

        var client = Build([("m1", first), ("m2", second)], maxRetries: 1);

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("from-second", response.Messages[^1].Text);
        Assert.AreEqual(1, first.CallCount, "A 429 must not be retried on the throttled model");
        Assert.AreEqual(1, second.CallCount);
    }

    [TestMethod]
    public async Task RateLimited_ModelIsSkippedUntilRetryAfterElapses()
    {
        var time   = new ManualTimeProvider();
        var first  = new SequentialStub();
        first.Enqueue(RateLimitEx(("retry-after", "20")));
        first.Enqueue(OkResponse("from-first"));
        var second = new FixedStub(response: OkResponse("from-second"));

        var client = new FallbackChatClient([("m1", first), ("m2", second)], NullLogger.Instance,
            retryDelay: TimeSpan.Zero, timeProvider: time);

        await client.GetResponseAsync([]);
        time.Advance(TimeSpan.FromSeconds(10));
        var whilePaused = await client.GetResponseAsync([]);

        Assert.AreEqual("from-second", whilePaused.Messages[^1].Text);
        Assert.AreEqual(1, first.CallCount, "A paused model must not be called inside its retry-after window");

        time.Advance(TimeSpan.FromSeconds(11));
        var afterPause = await client.GetResponseAsync([]);

        Assert.AreEqual("from-first", afterPause.Messages[^1].Text, "The primary is used again once the window closes");
        Assert.AreEqual(2, first.CallCount);
    }

    [TestMethod]
    public async Task RateLimited_WithoutRetryAfter_UsesDefaultPause()
    {
        var time   = new ManualTimeProvider();
        var first  = new FixedStub(ex: ClientResultEx(429)); // no readable headers
        var second = new FixedStub(response: OkResponse("from-second"));

        var client = new FallbackChatClient([("m1", first), ("m2", second)], NullLogger.Instance,
            retryDelay: TimeSpan.Zero, defaultRateLimitPause: TimeSpan.FromSeconds(30), timeProvider: time);

        await client.GetResponseAsync([]);
        time.Advance(TimeSpan.FromSeconds(29));
        await client.GetResponseAsync([]);
        Assert.AreEqual(1, first.CallCount, "Still inside the default pause");

        time.Advance(TimeSpan.FromSeconds(2));
        await client.GetResponseAsync([]);
        Assert.AreEqual(2, first.CallCount, "Default pause has elapsed");
    }

    [TestMethod]
    public async Task RateLimited_PausedModelIsStillTried_WhenNothingElseIsAvailable()
    {
        var time   = new ManualTimeProvider();
        var first  = new SequentialStub();
        first.Enqueue(RateLimitEx(("retry-after", "60")));
        first.Enqueue(OkResponse("from-first"));
        var second = new FixedStub(ex: HttpEx(HttpStatusCode.Unauthorized)); // degrades permanently

        var client = new FallbackChatClient([("m1", first), ("m2", second)], NullLogger.Instance,
            retryDelay: TimeSpan.Zero, maxRetries: 0, timeProvider: time);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.GetResponseAsync([]));

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("from-first", response.Messages[^1].Text,
            "With the fallback degraded, the paused primary is tried rather than failing the call");
    }

    [TestMethod]
    public async Task RateLimited_LastModel_WaitsOutRetryAfterAndRetries()
    {
        var only = new SequentialStub();
        only.Enqueue(RateLimitEx(("retry-after-ms", "5")));
        only.Enqueue(OkResponse("after-wait"));

        var client = Build([("m1", only)], maxRetries: 1);

        var response = await client.GetResponseAsync([]);

        Assert.AreEqual("after-wait", response.Messages[^1].Text);
        Assert.AreEqual(2, only.CallCount);
    }

    [TestMethod]
    public void GetRetryAfter_PrefersMillisecondsHeader()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(1500),
            FallbackChatClient.GetRetryAfter(RateLimitEx(("retry-after-ms", "1500"), ("retry-after", "2"))));
        Assert.AreEqual(TimeSpan.FromSeconds(7),
            FallbackChatClient.GetRetryAfter(RateLimitEx(("retry-after", "7"))));
        Assert.IsNull(FallbackChatClient.GetRetryAfter(RateLimitEx()));
        Assert.IsNull(FallbackChatClient.GetRetryAfter(ClientResultEx(429)), "Unreadable headers yield null, not a throw");
        Assert.IsNull(FallbackChatClient.GetRetryAfter(HttpEx(HttpStatusCode.TooManyRequests)));
    }

    [TestMethod]
    public void GetService_DelegatesToActiveClient()
    {
        var metadata = new ChatClientMetadata("test-provider", null, "model-x");
        var stub     = new ServiceStub(typeof(ChatClientMetadata), metadata);

        var client = Build([("model-x", stub)]);

        var result = client.GetService(typeof(ChatClientMetadata));

        Assert.AreSame(metadata, result);
    }
}
