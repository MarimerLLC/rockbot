using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Provides the current date and time in the agent's configured timezone.
/// Reads <c>Agent:Timezone</c> from configuration (IANA ID, e.g. "America/Chicago").
/// Falls back to <see cref="TimeZoneInfo.Local"/> when the setting is absent or unrecognized.
/// </summary>
public sealed class AgentClock
{
    /// <summary>The timezone the agent operates in.</summary>
    public TimeZoneInfo Zone { get; }

    public AgentClock(IConfiguration config, ILogger<AgentClock> logger)
    {
        var tzId = config["Agent:Timezone"];
        if (string.IsNullOrWhiteSpace(tzId))
        {
            Zone = TimeZoneInfo.Local;
            return;
        }

        try
        {
            Zone = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (TimeZoneNotFoundException)
        {
            logger.LogWarning(
                "Unknown timezone '{TzId}' in Agent:Timezone — falling back to local ({Local})",
                tzId, TimeZoneInfo.Local.Id);
            Zone = TimeZoneInfo.Local;
        }
    }

    /// <summary>Current date and time in the agent's configured timezone.</summary>
    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone);
}
