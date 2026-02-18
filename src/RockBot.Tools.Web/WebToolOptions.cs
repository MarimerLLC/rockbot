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
    /// Maximum number of characters to return from a browsed page.
    /// Content exceeding this limit is truncated with a notice.
    /// Defaults to 8000.
    /// </summary>
    public int MaxBrowseContentLength { get; set; } = 8000;
}
