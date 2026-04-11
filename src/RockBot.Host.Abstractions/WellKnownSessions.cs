namespace RockBot.Host;

/// <summary>
/// Well-known session identifiers shared across host components.
/// </summary>
public static class WellKnownSessions
{
    /// <summary>
    /// The primary Blazor UI user session. Used by idle-detection and
    /// availability checks to determine whether the user is actively engaged.
    /// </summary>
    public const string Primary = "blazor-session";
}
