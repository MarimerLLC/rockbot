namespace RockBot.Tools.Rest;

/// <summary>
/// Defines a REST endpoint that can be invoked as a tool.
/// </summary>
public sealed class RestEndpointDefinition
{
    /// <summary>
    /// Tool name used for registry lookup.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Description of what this endpoint does.
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    /// URL template with {placeholder} tokens for argument substitution.
    /// Example: "https://api.weather.com/v1/forecast?city={city}"
    /// </summary>
    public required string UrlTemplate { get; set; }

    /// <summary>
    /// HTTP method (GET, POST, PUT, DELETE). Defaults to GET.
    /// </summary>
    public string Method { get; set; } = "GET";

    /// <summary>
    /// JSON Schema string describing the tool's parameters, or null if none.
    /// </summary>
    public string? ParametersSchema { get; set; }

    /// <summary>
    /// Authentication type: "none", "bearer", or "api_key".
    /// </summary>
    public string AuthType { get; set; } = "none";

    /// <summary>
    /// Environment variable name containing the auth token/key.
    /// </summary>
    public string? AuthEnvVar { get; set; }

    /// <summary>
    /// Header name for API key authentication. Defaults to "X-Api-Key".
    /// </summary>
    public string ApiKeyHeader { get; set; } = "X-Api-Key";

    /// <summary>
    /// Whether to send arguments as JSON body for POST/PUT requests.
    /// </summary>
    public bool SendBodyAsJson { get; set; }
}
