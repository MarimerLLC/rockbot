using System.Text.Json.Serialization;

namespace RockBot.Wisp;

/// <summary>
/// Defines behavior when a <c>direct</c> step fails.
/// </summary>
public sealed record OnFailureAction
{
    /// <summary>
    /// Action to take: "abort" (default) or "skip_to".
    /// </summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>
    /// Step ID to skip to when action is "skip_to".
    /// </summary>
    [JsonPropertyName("skip_to")]
    public string? SkipTo { get; init; }
}
