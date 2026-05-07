using System.Net;

namespace RockBot.Host.Tests;

[TestClass]
public class DefaultLlmRateLimitClassifierTests
{
    private readonly DefaultLlmRateLimitClassifier _classifier = new();

    [TestMethod]
    public void TryClassify_NonRateLimitException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("boom");
        var result = _classifier.TryClassify(ex, out var retryAfter);
        Assert.IsFalse(result);
        Assert.IsNull(retryAfter);
    }

    [TestMethod]
    public void TryClassify_HttpRequestException_429_ReturnsTrue()
    {
        var ex = new HttpRequestException(
            HttpRequestError.Unknown,
            "rate limited",
            inner: null,
            statusCode: HttpStatusCode.TooManyRequests);

        var result = _classifier.TryClassify(ex, out var retryAfter);
        Assert.IsTrue(result);
        // HttpRequestException carries no headers, so no Retry-After is extracted.
        Assert.IsNull(retryAfter);
    }

    [TestMethod]
    public void TryClassify_HttpRequestException_NotRateLimit_ReturnsFalse()
    {
        var ex = new HttpRequestException(
            HttpRequestError.Unknown,
            "server error",
            inner: null,
            statusCode: HttpStatusCode.InternalServerError);

        var result = _classifier.TryClassify(ex, out var retryAfter);
        Assert.IsFalse(result);
        Assert.IsNull(retryAfter);
    }

    [TestMethod]
    public void TryClassify_WrappedRateLimit_WalksInnerExceptions()
    {
        var inner = new HttpRequestException(
            HttpRequestError.Unknown,
            "rate limited",
            inner: null,
            statusCode: HttpStatusCode.TooManyRequests);
        var outer = new InvalidOperationException("wrapper", inner);

        var result = _classifier.TryClassify(outer, out _);
        Assert.IsTrue(result, "Classifier must walk the inner-exception chain");
    }

    [TestMethod]
    public void TryClassify_Aggregate_DoesNotWalkInnerExceptionsCollection()
    {
        // Document current behavior: AggregateException's InnerExceptions collection is
        // not walked. Only AggregateException.InnerException (the first inner) is.
        // If a future caller wraps via AggregateException, this test will fail and force
        // an explicit decision.
        var rateLimit = new HttpRequestException(
            HttpRequestError.Unknown,
            "rate limited",
            inner: null,
            statusCode: HttpStatusCode.TooManyRequests);
        var agg = new AggregateException(new InvalidOperationException("first"), rateLimit);

        var result = _classifier.TryClassify(agg, out _);
        // agg.InnerException is the first one (InvalidOperationException), not the
        // rate-limit one. Classifier walks only the linear chain.
        Assert.IsFalse(result);
    }
}
