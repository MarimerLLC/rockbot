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
    /// Environment variable name containing the search API key.
    /// </summary>
    public string ApiKeyEnvVar { get; set; } = "BRAVE_API_KEY";

    /// <summary>
    /// Maximum number of search results to return. Defaults to 10.
    /// </summary>
    public int MaxSearchResults { get; set; } = 10;
}
