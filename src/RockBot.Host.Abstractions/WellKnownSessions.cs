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

    /// <summary>
    /// Session carrying unsolicited output from system-owned scheduled work — system scheduled
    /// tasks and the memory audit's attention alerts. Frontends categorize this separately from
    /// user-requested scheduled tasks so housekeeping does not read as a reply.
    /// </summary>
    public const string ScheduledSystem = "scheduled-system";
}
