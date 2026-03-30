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
        stub.Enqueue(HttpEx(HttpStatusCode.TooManyRequests)); // attempt 0: 429
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
