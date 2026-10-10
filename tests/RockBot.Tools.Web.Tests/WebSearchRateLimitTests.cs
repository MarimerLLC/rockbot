using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Tools.Web.Brave;

namespace RockBot.Tools.Web.Tests;

/// <summary>
/// Throttling and 429/503 retry for web_search (issue #676). These use real time with short
/// intervals; timing assertions allow a small tolerance for timer granularity.
/// </summary>
[TestClass]
public class WebSearchRateLimitTests
{
    private const string OkBody = """
        { "web": { "results": [ { "title": "T", "url": "https://example.com", "description": "S" } ] } }
        """;

    // Timers can fire a few ms either side of the due time relative to Stopwatch.
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(25);

    private static WebToolOptions FastOptions(
        int minIntervalMs = 0, int baseDelayMs = 20, int maxDelayMs = 200, int maxRetries = 3) => new()
    {
        ApiKey = "test-key",
        SearchMinIntervalMs = minIntervalMs,
        SearchRetryBaseDelayMs = baseDelayMs,
        SearchRetryMaxDelayMs = maxDelayMs,
        SearchMaxRetries = maxRetries
    };

    private static BraveSearchProvider MakeProvider(HttpMessageHandler handler, WebToolOptions options) =>
        new(new StubFactory(handler), options, NullLogger<BraveSearchProvider>.Instance);

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent(OkBody, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode status, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is not null)
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private static ToolInvokeRequest SearchRequest(string query) => new()
    {
        ToolCallId = "call_" + query,
        ToolName = "web_search",
        Arguments = $$"""{"query": "{{query}}"}"""
    };

    // ── Retry on 429 / 503 ────────────────────────────────────────────────────

    [TestMethod]
    public async Task SearchAsync_RetriesAfter429WithoutRetryAfter_ThenSucceeds()
    {
        var handler = new SequenceHandler(
            () => Status(HttpStatusCode.TooManyRequests),
            Ok);
        var provider = MakeProvider(handler, FastOptions(baseDelayMs: 60));

        var results = await provider.SearchAsync("q", 5, CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(2, handler.Starts.Count);
        // No Retry-After: first backoff is the base delay.
        Assert.IsTrue(handler.Starts[1] - handler.Starts[0] >= TimeSpan.FromMilliseconds(60) - Tolerance,
            $"retry came after {(handler.Starts[1] - handler.Starts[0]).TotalMilliseconds} ms");
    }

    [TestMethod]
    public async Task SearchAsync_HonoursRetryAfterSeconds_ThenSucceeds()
    {
        var handler = new SequenceHandler(
            () => Status(HttpStatusCode.TooManyRequests, retryAfter: "1"),
            Ok);
        // Base delay is tiny, so a ~1 s gap can only come from the Retry-After header.
        var provider = MakeProvider(handler, FastOptions(baseDelayMs: 10, maxDelayMs: 5000));

        var results = await provider.SearchAsync("q", 5, CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(2, handler.Starts.Count);
        Assert.IsTrue(handler.Starts[1] - handler.Starts[0] >= TimeSpan.FromSeconds(1) - Tolerance,
            $"retry came after {(handler.Starts[1] - handler.Starts[0]).TotalMilliseconds} ms");
    }

    [TestMethod]
    public async Task SearchAsync_RetriesAfter503_ThenSucceeds()
    {
        var handler = new SequenceHandler(
            () => Status(HttpStatusCode.ServiceUnavailable),
            Ok);
        var provider = MakeProvider(handler, FastOptions());

        var results = await provider.SearchAsync("q", 5, CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(2, handler.Starts.Count);
    }

    [TestMethod]
    public async Task SearchAsync_DoesNotRetryOtherErrors()
    {
        var handler = new SequenceHandler(() => Status(HttpStatusCode.InternalServerError), Ok);
        var provider = MakeProvider(handler, FastOptions());

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.SearchAsync("q", 5, CancellationToken.None));
        Assert.AreEqual(1, handler.Starts.Count);
    }

    [TestMethod]
    public async Task SearchAsync_Persistent429_ThrowsRateLimitedAfterThreeRetries()
    {
        var handler = new SequenceHandler(() => Status(HttpStatusCode.TooManyRequests));
        var provider = MakeProvider(handler, FastOptions(baseDelayMs: 10, maxDelayMs: 40));

        var ex = await Assert.ThrowsExactlyAsync<WebSearchRateLimitedException>(
            () => provider.SearchAsync("q", 5, CancellationToken.None));

        Assert.AreEqual(3, ex.Retries);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.AreEqual(4, handler.Starts.Count, "one attempt plus three retries");
    }

    [TestMethod]
    public async Task ExecuteAsync_Persistent429_ReturnsActionableError()
    {
        var handler = new SequenceHandler(() => Status(HttpStatusCode.TooManyRequests));
        var options = FastOptions(baseDelayMs: 10, maxDelayMs: 40);
        var executor = new WebSearchToolExecutor(MakeProvider(handler, options), options);

        var response = await executor.ExecuteAsync(SearchRequest("q"), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        Assert.AreEqual(
            "web_search was rate-limited by the search provider (HTTP 429) after 3 retries — " +
            "wait a few seconds and retry this query, or continue with web_browse on a known URL.",
            response.Content);
    }

    [TestMethod]
    public async Task SearchAsync_RetriesDisabled_FailsOnFirst429()
    {
        var handler = new SequenceHandler(() => Status(HttpStatusCode.TooManyRequests));
        var provider = MakeProvider(handler, FastOptions(maxRetries: 0));

        var ex = await Assert.ThrowsExactlyAsync<WebSearchRateLimitedException>(
            () => provider.SearchAsync("q", 5, CancellationToken.None));

        Assert.AreEqual(0, ex.Retries);
        Assert.AreEqual(1, handler.Starts.Count);
    }

    // ── Spacing and serialization ─────────────────────────────────────────────

    [TestMethod]
    public async Task SearchAsync_ThreeConcurrent_AllSucceedAndAreSpacedByMinInterval()
    {
        var interval = TimeSpan.FromMilliseconds(150);
        // The fake provider allows one request per 120 ms and answers 429 to anything faster,
        // so a single 429 would show the spacing failed.
        var handler = new QuotaHandler(TimeSpan.FromMilliseconds(120));
        var provider = MakeProvider(handler, FastOptions(minIntervalMs: (int)interval.TotalMilliseconds, maxRetries: 0));

        var searches = Enumerable.Range(0, 3)
            .Select(i => provider.SearchAsync($"q{i}", 5, CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(searches);

        Assert.IsTrue(results.All(r => r.Count == 1));
        Assert.AreEqual(0, handler.Rejected, "no request should have hit the provider's quota");
        var starts = handler.Starts.OrderBy(t => t).ToList();
        Assert.AreEqual(3, starts.Count);
        for (var i = 1; i < starts.Count; i++)
        {
            Assert.IsTrue(starts[i] - starts[i - 1] >= interval - Tolerance,
                $"requests {i - 1} and {i} started {(starts[i] - starts[i - 1]).TotalMilliseconds} ms apart");
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_ThreeConcurrentCallsAgainstQuota_AllGetThroughViaRetry()
    {
        // Acceptance for #676: with spacing off, the provider rejects the second and third of
        // three concurrent calls with 429, and retry still gets all three through.
        var handler = new QuotaHandler(TimeSpan.FromMilliseconds(50));
        var options = FastOptions(minIntervalMs: 0, baseDelayMs: 80, maxDelayMs: 320);
        var executor = new WebSearchToolExecutor(MakeProvider(handler, options), options);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(i => executor.ExecuteAsync(SearchRequest($"q{i}"), CancellationToken.None)));

        Assert.IsTrue(responses.All(r => !r.IsError), string.Join(" | ", responses.Select(r => r.Content)));
        Assert.IsTrue(handler.Rejected >= 2, $"expected the quota to reject at least two requests, saw {handler.Rejected}");
        Assert.AreEqual(3, handler.Accepted);
    }

    [TestMethod]
    public async Task SearchAsync_SerializesCalls_EvenWithNoSpacing()
    {
        var handler = new ConcurrencyTrackingHandler();
        var provider = MakeProvider(handler, FastOptions(minIntervalMs: 0));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => provider.SearchAsync($"q{i}", 5, CancellationToken.None)));

        Assert.AreEqual(4, handler.Calls);
        Assert.AreEqual(1, handler.MaxConcurrent);
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SearchAsync_CancelledWhileWaitingForSpacing_Throws()
    {
        var handler = new SequenceHandler(Ok);
        var provider = MakeProvider(handler, FastOptions(minIntervalMs: 30_000));
        await provider.SearchAsync("first", 5, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.SearchAsync("second", 5, cts.Token));

        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(10), "cancellation should end the spacing wait");
        Assert.AreEqual(1, handler.Starts.Count, "the cancelled search must not reach the provider");
    }

    [TestMethod]
    public async Task SearchAsync_CancelledDuringRetryDelay_Throws()
    {
        var handler = new SequenceHandler(() => Status(HttpStatusCode.TooManyRequests, retryAfter: "30"), Ok);
        var provider = MakeProvider(handler, FastOptions(maxDelayMs: 60_000));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.SearchAsync("q", 5, cts.Token));

        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(10), "cancellation should end the retry wait");
        Assert.AreEqual(1, handler.Starts.Count);
    }

    [TestMethod]
    public async Task SearchAsync_CancelledWhileQueuedBehindAnotherSearch_Throws()
    {
        var handler = new GateHandler();
        var provider = MakeProvider(handler, FastOptions());

        var first = provider.SearchAsync("first", 5, CancellationToken.None);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.SearchAsync("second", 5, cts.Token));

        handler.Release.SetResult();
        var results = await first;
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(1, handler.Calls);

        // The queue still works after a waiter gave up.
        var third = await provider.SearchAsync("third", 5, CancellationToken.None);
        Assert.AreEqual(1, third.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_PropagatesCallerCancellation()
    {
        var handler = new SequenceHandler(() => Status(HttpStatusCode.TooManyRequests, retryAfter: "30"), Ok);
        var options = FastOptions(maxDelayMs: 60_000);
        var executor = new WebSearchToolExecutor(MakeProvider(handler, options), options);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(SearchRequest("q"), cts.Token));
    }

    // ── Telemetry ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SearchAsync_Persistent429_CountsRetriedAndExhausted()
    {
        long retried = 0, exhausted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ToolDiagnostics.MeterName &&
                instrument.Name == WebToolDiagnostics.RateLimitedInstrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key != "outcome") continue;
                if ((string?)tag.Value == "retried") Interlocked.Add(ref retried, value);
                if ((string?)tag.Value == "exhausted") Interlocked.Add(ref exhausted, value);
            }
        });
        listener.Start();

        var handler = new SequenceHandler(() => Status(HttpStatusCode.TooManyRequests));
        var provider = MakeProvider(handler, FastOptions(baseDelayMs: 5, maxDelayMs: 20));
        await Assert.ThrowsExactlyAsync<WebSearchRateLimitedException>(
            () => provider.SearchAsync("q", 5, CancellationToken.None));

        Assert.IsTrue(Interlocked.Read(ref retried) >= 3);
        Assert.IsTrue(Interlocked.Read(ref exhausted) >= 1);
    }

    // ── Retry delay policy ────────────────────────────────────────────────────

    [TestMethod]
    public void ComputeDelay_NoRetryAfter_BacksOffExponentiallyFromOneSecond_CappedAtEight()
    {
        var options = new WebToolOptions();
        using var response = Status(HttpStatusCode.TooManyRequests);
        var now = DateTimeOffset.UtcNow;

        var delays = Enumerable.Range(1, 5)
            .Select(n => SearchRetryPolicy.ComputeDelay(response, n, options, now).TotalSeconds)
            .ToArray();

        CollectionAssert.AreEqual(new double[] { 1, 2, 4, 8, 8 }, delays);
    }

    [TestMethod]
    public void ComputeDelay_RetryAfterSeconds_IsHonoured()
    {
        using var response = Status(HttpStatusCode.TooManyRequests, retryAfter: "3");

        var delay = SearchRetryPolicy.ComputeDelay(response, 1, new WebToolOptions(), DateTimeOffset.UtcNow);

        Assert.AreEqual(TimeSpan.FromSeconds(3), delay);
    }

    [TestMethod]
    public void ComputeDelay_RetryAfterHttpDate_IsHonoured()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        using var response = Status(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(5));

        var delay = SearchRetryPolicy.ComputeDelay(response, 1, new WebToolOptions(), now);

        Assert.AreEqual(TimeSpan.FromSeconds(5), delay);
    }

    [TestMethod]
    public void ComputeDelay_RetryAfterHttpDateInThePast_IsZero()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        using var response = Status(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(-5));

        var delay = SearchRetryPolicy.ComputeDelay(response, 1, new WebToolOptions(), now);

        Assert.AreEqual(TimeSpan.Zero, delay);
    }

    [TestMethod]
    public void ComputeDelay_RetryAfterBeyondCap_IsCapped()
    {
        using var response = Status(HttpStatusCode.TooManyRequests, retryAfter: "120");

        var delay = SearchRetryPolicy.ComputeDelay(response, 1, new WebToolOptions(), DateTimeOffset.UtcNow);

        Assert.AreEqual(TimeSpan.FromSeconds(8), delay);
    }

    [TestMethod]
    public void IsRateLimited_OnlyFor429And503()
    {
        Assert.IsTrue(SearchRetryPolicy.IsRateLimited(HttpStatusCode.TooManyRequests));
        Assert.IsTrue(SearchRetryPolicy.IsRateLimited(HttpStatusCode.ServiceUnavailable));
        Assert.IsFalse(SearchRetryPolicy.IsRateLimited(HttpStatusCode.InternalServerError));
        Assert.IsFalse(SearchRetryPolicy.IsRateLimited(HttpStatusCode.Unauthorized));
        Assert.IsFalse(SearchRetryPolicy.IsRateLimited(HttpStatusCode.OK));
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        // disposeHandler: false — the provider disposes its client after every search.
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Answers with each factory in turn, repeating the last one.</summary>
    private sealed class SequenceHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<TimeSpan> _starts = [];
        private int _next;

        public IReadOnlyList<TimeSpan> Starts { get { lock (_starts) return _starts.ToList(); } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Func<HttpResponseMessage> factory;
            lock (_starts)
            {
                _starts.Add(_clock.Elapsed);
                factory = responses[Math.Min(_next++, responses.Length - 1)];
            }
            return Task.FromResult(factory());
        }
    }

    /// <summary>
    /// Simulates a provider quota: one request per <c>window</c>. Anything sooner gets 429
    /// with no Retry-After header.
    /// </summary>
    private sealed class QuotaHandler(TimeSpan window) : HttpMessageHandler
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<TimeSpan> _starts = [];
        private TimeSpan? _lastAccepted;

        public int Accepted { get; private set; }
        public int Rejected { get; private set; }
        public IReadOnlyList<TimeSpan> Starts { get { lock (_starts) return _starts.ToList(); } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_starts)
            {
                var now = _clock.Elapsed;
                _starts.Add(now);
                if (_lastAccepted is { } last && now - last < window)
                {
                    Rejected++;
                    return Task.FromResult(Status(HttpStatusCode.TooManyRequests));
                }
                _lastAccepted = now;
                Accepted++;
                return Task.FromResult(Ok());
            }
        }
    }

    private sealed class ConcurrencyTrackingHandler : HttpMessageHandler
    {
        private int _inFlight;
        private int _maxConcurrent;
        private int _calls;

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxConcurrent)) &&
                   Interlocked.CompareExchange(ref _maxConcurrent, now, seen) != seen) { }
            try
            {
                await Task.Delay(20, ct);
                return Ok();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>Holds the first request open until <see cref="Release"/> is set.</summary>
    private sealed class GateHandler : HttpMessageHandler
    {
        private int _calls;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return Ok();
        }
    }
}
