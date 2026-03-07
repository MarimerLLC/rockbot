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
        int maxRetries = 1) =>
        new(entries, NullLogger.Instance, retryDelay: TimeSpan.Zero, maxRetries: maxRetries);

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
    public void GetService_DelegatesToActiveClient()
    {
        var metadata = new ChatClientMetadata("test-provider", null, "model-x");
        var stub     = new ServiceStub(typeof(ChatClientMetadata), metadata);

        var client = Build([("model-x", stub)]);

        var result = client.GetService(typeof(ChatClientMetadata));

        Assert.AreSame(metadata, result);
    }
}
