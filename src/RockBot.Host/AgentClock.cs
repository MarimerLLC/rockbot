using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Provides the current date and time in the agent's configured timezone.
/// The active timezone is resolved in priority order:
/// <list type="number">
///   <item>The <c>timezone</c> file in the agent profile directory (written by <see cref="SetZoneAsync"/>)</item>
///   <item><c>Agent:Timezone</c> configuration (IANA ID, e.g. "America/Chicago")</item>
///   <item><see cref="TimeZoneInfo.Local"/> (UTC on a typical k8s node)</item>
/// </list>
/// The active timezone can be changed at runtime via <see cref="SetZoneAsync"/>, which
/// also persists the new value to the profile directory so it survives restarts.
/// </summary>
public sealed class AgentClock
{
    private readonly string _persistPath;
    private readonly ILogger<AgentClock> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TimeZoneInfo _zone;

    /// <summary>The timezone the agent is currently operating in.</summary>
    public TimeZoneInfo Zone => _zone;

    /// <summary>Current date and time in the agent's active timezone.</summary>
    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _zone);

    public AgentClock(
        IConfiguration config,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<AgentClock> logger)
    {
        _logger = logger;

        var baseDir = profileOptions.Value.BasePath;
        if (!Path.IsPathRooted(baseDir))
            baseDir = Path.Combine(AppContext.BaseDirectory, baseDir);

        _persistPath = Path.Combine(baseDir, "timezone");
        _logger.LogInformation("AgentClock timezone persist path: {Path}", _persistPath);
        _zone = LoadZone(config);
    }

    /// <summary>
    /// Updates the active timezone and persists it to the profile directory.
    /// Takes effect immediately for all subsequent calls to <see cref="Now"/> and <see cref="Zone"/>.
    /// </summary>
    public async Task SetZoneAsync(TimeZoneInfo zone)
    {
        await _lock.WaitAsync();
        try
        {
            _zone = zone;

            var dir = Path.GetDirectoryName(_persistPath)!;
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_persistPath, zone.Id);

            _logger.LogInformation("Timezone updated to {ZoneId} ({DisplayName})", zone.Id, zone.DisplayName);
        }
        finally
        {
            _lock.Release();
        }
    }

    private TimeZoneInfo LoadZone(IConfiguration config)
    {
        // 1. Persisted file — written by SetZoneAsync when the user updates their timezone
        if (File.Exists(_persistPath))
        {
            var id = File.ReadAllText(_persistPath).Trim();
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
                    _logger.LogInformation("Timezone loaded from file: {ZoneId}", zone.Id);
                    return zone;
                }
                catch (TimeZoneNotFoundException)
                {
                    _logger.LogWarning(
                        "Unrecognized timezone '{Id}' in {Path} — ignoring",
                        id, _persistPath);
                }
            }
        }

        // 2. Deployment config (Agent:Timezone)
        var tzId = config["Agent:Timezone"];
        if (!string.IsNullOrWhiteSpace(tzId))
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(tzId);
                _logger.LogInformation("Timezone loaded from config: {ZoneId}", zone.Id);
                return zone;
            }
            catch (TimeZoneNotFoundException)
            {
                _logger.LogWarning(
                    "Unknown timezone '{TzId}' in Agent:Timezone — falling back to local ({Local})",
                    tzId, TimeZoneInfo.Local.Id);
            }
        }

        // 3. System local (UTC on a typical k8s node)
        _logger.LogInformation("Timezone not configured — using system local: {ZoneId}", TimeZoneInfo.Local.Id);
        return TimeZoneInfo.Local;
    }
}
