namespace RockBot.Tools.Rest;

/// <summary>
/// Configuration for REST tool endpoints.
/// </summary>
public sealed class RestToolOptions
{
    /// <summary>
    /// REST endpoint definitions to register as tools.
    /// </summary>
    public List<RestEndpointDefinition> Endpoints { get; } = [];
}
