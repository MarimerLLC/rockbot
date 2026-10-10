namespace RockBot.Tools.Web;

/// <summary>
/// Thrown by an <see cref="IWebSearchProvider"/> when the provider kept answering with a
/// rate-limit or overload status (HTTP 429 or 503) after every retry. The message is written for
/// the model to act on: wait and retry the query, or continue another way, rather than giving
/// up on that line of research as it does with a raw HTTP error.
/// </summary>
public sealed class WebSearchRateLimitedException : HttpRequestException
{
    public WebSearchRateLimitedException(int statusCode, int retries)
        : base(BuildMessage(statusCode, retries), inner: null, (System.Net.HttpStatusCode)statusCode)
    {
        Retries = retries;
    }

    /// <summary>Number of retries made after the first attempt.</summary>
    public int Retries { get; }

    private static string BuildMessage(int statusCode, int retries)
    {
        var reason = statusCode == 503
            ? $"was throttled by the search provider (HTTP {statusCode}, service unavailable)"
            : $"was rate-limited by the search provider (HTTP {statusCode})";
        var retryText = retries == 1 ? "1 retry" : $"{retries} retries";
        return $"web_search {reason} after {retryText} — wait a few seconds and retry this query, " +
               "or continue with web_browse on a known URL.";
    }
}
