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
/// That resolved zone is the user's <see cref="HomeZone"/>. It can be changed at runtime via
/// <see cref="SetZoneAsync"/>, which also persists the new value to the profile directory so it
/// survives restarts.
/// <para>
/// A location source (see <see cref="UserLocationService"/>) can lay an in-memory current zone over
/// the home zone with <see cref="SetCurrentZone"/> while the user is travelling. <see cref="Zone"/>
/// and <see cref="Now"/> follow that override, so every consumer tracks the user's local time.
/// Without an override, <see cref="Zone"/> is the home zone.
/// </para>
/// </summary>
public sealed class AgentClock
{
    private readonly string _persistPath;
    private readonly ILogger<AgentClock> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TimeZoneInfo _zone;
    private volatile TimeZoneInfo? _currentOverride;

    /// <summary>
    /// The timezone the agent is currently operating in: the user's current zone when a location
    /// source has set one, otherwise <see cref="HomeZone"/>.
    /// </summary>
    public TimeZoneInfo Zone => _currentOverride ?? _zone;

    /// <summary>
    /// The user's configured (home) timezone: the persisted file, then <c>Agent:Timezone</c>,
    /// then the system local zone. Set by <see cref="SetZoneAsync"/>.
    /// </summary>
    public TimeZoneInfo HomeZone => _zone;

    /// <summary>True when a current zone is set and its UTC offset rules differ from the home zone.</summary>
    public bool IsAwayFromHome => _currentOverride is { } current && !SameZone(current, _zone);

    /// <summary>
    /// Raised when <see cref="SetCurrentZone"/> changes the effective <see cref="Zone"/>.
    /// Not raised by <see cref="SetZoneAsync"/>.
    /// </summary>
    public event EventHandler? ZoneChanged;

    /// <summary>Current date and time in the agent's active timezone.</summary>
    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone);

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
    /// Updates the user's home timezone (<see cref="HomeZone"/>) and persists it to the profile
    /// directory. Takes effect immediately for all subsequent calls to <see cref="Now"/> and
    /// <see cref="Zone"/> unless a current zone set by <see cref="SetCurrentZone"/> overrides it.
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

    /// <summary>
    /// Sets the user's current zone, in memory only, overriding <see cref="HomeZone"/> for
    /// <see cref="Zone"/> and <see cref="Now"/>. Pass <c>null</c> to revert to the home zone.
    /// Raises <see cref="ZoneChanged"/> when the effective zone actually changes.
    /// </summary>
    public void SetCurrentZone(TimeZoneInfo? zone)
    {
        var before = Zone;
        _currentOverride = zone;
        var after = Zone;

        if (before.Id == after.Id)
            return;

        _logger.LogInformation("Current timezone changed from {From} to {To} (home {Home})",
            before.Id, after.Id, _zone.Id);
        ZoneChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Spec meaning "the user's home zone" (also the meaning of a null spec).</summary>
    public const string HomeSpec = "home";

    /// <summary>Spec meaning "wherever the user currently is".</summary>
    public const string LocalSpec = "local";

    /// <summary>
    /// Resolves a scheduled-task zone spec: null or <c>"home"</c> → <see cref="HomeZone"/>,
    /// <c>"local"</c> → <see cref="Zone"/>, otherwise an IANA id. An unknown id falls back to
    /// <see cref="HomeZone"/> with a warning.
    /// </summary>
    public TimeZoneInfo ResolveZone(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.Equals(HomeSpec, StringComparison.OrdinalIgnoreCase))
            return HomeZone;
        if (spec.Equals(LocalSpec, StringComparison.OrdinalIgnoreCase))
            return Zone;

        if (TimeZoneInfo.TryFindSystemTimeZoneById(spec.Trim(), out var zone))
            return zone;

        _logger.LogWarning("Unknown timezone '{Spec}' — using home zone {Home}", spec, _zone.Id);
        return HomeZone;
    }

    /// <summary>True when <paramref name="spec"/> is <c>"home"</c>, <c>"local"</c>, or a known IANA id.</summary>
    public static bool IsValidSpec(string? spec) =>
        !string.IsNullOrWhiteSpace(spec)
        && (spec.Equals(HomeSpec, StringComparison.OrdinalIgnoreCase)
            || spec.Equals(LocalSpec, StringComparison.OrdinalIgnoreCase)
            || TimeZoneInfo.TryFindSystemTimeZoneById(spec.Trim(), out _));

    private static bool SameZone(TimeZoneInfo a, TimeZoneInfo b) =>
        a.Id == b.Id || a.HasSameRules(b);

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
