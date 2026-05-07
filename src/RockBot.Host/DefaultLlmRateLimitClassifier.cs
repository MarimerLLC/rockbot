using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;

namespace RockBot.Host;

/// <summary>
/// Default <see cref="ILlmRateLimitClassifier"/>: detects rate-limit errors
/// surfaced by the OpenAI SDK (via <see cref="ClientResultException"/>) and
/// generic <see cref="HttpRequestException"/>s carrying a 429 status. Walks the
/// exception chain so wrapped errors are caught.
/// </summary>
internal sealed class DefaultLlmRateLimitClassifier : ILlmRateLimitClassifier
{
    public bool TryClassify(Exception exception, out TimeSpan? retryAfter)
    {
        retryAfter = null;

        var current = exception;
        while (current is not null)
        {
            if (current is ClientResultException cre && cre.Status == 429)
            {
                retryAfter = ParseRetryAfter(cre.GetRawResponse());
                return true;
            }

            if (current is HttpRequestException hre && hre.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // HttpRequestException does not carry response headers, so we
                // cannot extract Retry-After here. The gateway falls back to
                // exponential backoff.
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    private static TimeSpan? ParseRetryAfter(PipelineResponse? response)
    {
        if (response is null) return null;
        if (!response.Headers.TryGetValue("retry-after", out var raw) || string.IsNullOrEmpty(raw))
            return null;

        // Numeric form: integer seconds.
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        // HTTP-date form (RFC 7231).
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var when))
        {
            var diff = when - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }

        return null;
    }
}
