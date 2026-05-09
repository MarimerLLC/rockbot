namespace RockBot.Host;

/// <summary>
/// Identity of a tool-failure cluster. Server and tool are normalised to
/// lowercase so case differences from MCP responses don't fragment clusters.
/// See <c>design/self-repair.md</c> Phase 5.
/// </summary>
/// <param name="Server">MCP server name (lowercased on construction).</param>
/// <param name="Tool">Tool name on that server (lowercased on construction).</param>
/// <param name="ErrorClass">Deterministic class of error — usually a missing field name extracted from the error string, or <c>"unknown"</c>.</param>
public sealed record ClusterKey(string Server, string Tool, string ErrorClass)
{
    public string Server { get; } = NormaliseLowerOrThrow(Server, nameof(Server));
    public string Tool { get; } = NormaliseLowerOrThrow(Tool, nameof(Tool));
    public string ErrorClass { get; } = ValidateOrThrow(ErrorClass, nameof(ErrorClass));

    private static string NormaliseLowerOrThrow(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.ToLowerInvariant();
    }

    private static string ValidateOrThrow(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value;
    }
}
