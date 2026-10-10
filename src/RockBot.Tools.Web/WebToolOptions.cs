namespace RockBot.Tools.Web;

/// <summary>
/// Configuration options for web tools.
/// </summary>
public sealed class WebToolOptions
{
    /// <summary>
    /// The search provider to use. Defaults to "brave".
    /// </summary>
    public string SearchProvider { get; set; } = "brave";

    /// <summary>
    /// The search API key. Takes precedence over <see cref="ApiKeyEnvVar"/> when set.
    /// Store via dotnet user-secrets as "WebTools:ApiKey".
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Fallback environment variable name to read the API key from when
    /// <see cref="ApiKey"/> is not set. Defaults to "BRAVE_API_KEY".
    /// </summary>
    public string ApiKeyEnvVar { get; set; } = "BRAVE_API_KEY";

    /// <summary>
    /// Maximum number of search results to return. Defaults to 10.
    /// </summary>
    public int MaxSearchResults { get; set; } = 10;

    /// <summary>
    /// Minimum spacing in milliseconds between the starts of two search requests. Searches are
    /// also serialized, one at a time, across every session and subagent in the process, because
    /// the provider's quota belongs to the API key. Defaults to 1100, just over the 1 request per
    /// second allowed by Brave's base tiers; lower it on a plan with a higher per-second quota.
    /// 0 turns off the spacing but keeps searches serialized.
    /// </summary>
    public int SearchMinIntervalMs { get; set; } = 1100;

    /// <summary>
    /// How many times a search is retried after the provider answers HTTP 429 (Too Many Requests)
    /// or 503 (Service Unavailable). Defaults to 3. 0 disables retries.
    /// </summary>
    public int SearchMaxRetries { get; set; } = 3;

    /// <summary>
    /// First retry delay in milliseconds when the provider sends no <c>Retry-After</c> header.
    /// Each further retry doubles it, up to <see cref="SearchRetryMaxDelayMs"/>. Defaults to 1000.
    /// </summary>
    public int SearchRetryBaseDelayMs { get; set; } = 1000;

    /// <summary>
    /// Longest wait in milliseconds before one retry. It caps the exponential backoff and also a
    /// <c>Retry-After</c> value from the provider, so a single tool call cannot stall for minutes.
    /// Defaults to 8000.
    /// </summary>
    public int SearchRetryMaxDelayMs { get; set; } = 8000;

    /// <summary>
    /// Maximum number of characters to return from a browsed page.
    /// Content exceeding this limit is truncated with a notice.
    /// Defaults to 8000.
    /// </summary>
    public int MaxBrowseContentLength { get; set; } = 8000;

    /// <summary>
    /// Character threshold above which page content is chunked into working memory
    /// rather than returned inline. Defaults to 8000.
    /// </summary>
    public int ChunkingThreshold { get; set; } = 8_000;

    /// <summary>
    /// Maximum size in characters of each chunk saved to working memory (~5000 tokens).
    /// Defaults to 20000.
    /// </summary>
    public int ChunkMaxLength { get; set; } = 20_000;

    /// <summary>
    /// Time-to-live in minutes for web chunks saved to working memory. Defaults to 20.
    /// </summary>
    public int ChunkTtlMinutes { get; set; } = 20;
}
