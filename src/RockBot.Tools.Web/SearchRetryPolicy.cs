using System.Net;

namespace RockBot.Tools.Web;

/// <summary>
/// When to retry a search request, and how long to wait first. Provider-agnostic so any
/// HTTP-based <see cref="IWebSearchProvider"/> can share it.
/// </summary>
internal static class SearchRetryPolicy
{
    /// <summary>
    /// True for the statuses that mean "slow down and try again": 429 Too Many Requests and
    /// 503 Service Unavailable.
    /// </summary>
    public static bool IsRateLimited(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    /// <summary>
    /// The wait before retry number <paramref name="retryNumber"/> (1-based). A
    /// <c>Retry-After</c> header, in seconds or as an HTTP date, wins; otherwise the delay is
    /// <see cref="WebToolOptions.SearchRetryBaseDelayMs"/> doubled per retry. Both are capped at
    /// <see cref="WebToolOptions.SearchRetryMaxDelayMs"/>.
    /// </summary>
    public static TimeSpan ComputeDelay(
        HttpResponseMessage response, int retryNumber, WebToolOptions options, DateTimeOffset now)
    {
        var max = TimeSpan.FromMilliseconds(Math.Max(0, options.SearchRetryMaxDelayMs));

        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? requested = retryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - now,
            _ => null
        };

        TimeSpan delay;
        if (requested is { } r)
        {
            delay = r < TimeSpan.Zero ? TimeSpan.Zero : r;
        }
        else
        {
            var baseMs = Math.Max(0, options.SearchRetryBaseDelayMs);
            var exponent = Math.Clamp(retryNumber - 1, 0, 30);
            delay = TimeSpan.FromMilliseconds(Math.Min(baseMs * Math.Pow(2, exponent), max.TotalMilliseconds));
        }

        return delay > max ? max : delay;
    }
}
