namespace RockBot.Host;

/// <summary>
/// Configuration for following the user's location. Bound from the <c>UserLocation</c> section.
/// Location is disabled unless <see cref="OwnTracksOptions.BaseUrl"/> is set.
/// </summary>
public sealed class UserLocationOptions
{
    /// <summary>How often to poll the location source. Default 5 minutes.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Fixes older than this are ignored and the current zone reverts to home, so an
    /// uninstalled phone app doesn't leave the agent stuck on the last trip. Default 72 hours.
    /// </summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(72);

    /// <summary>OwnTracks Recorder source.</summary>
    public OwnTracksOptions OwnTracks { get; set; } = new();

    /// <summary>True when a location source is configured.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(OwnTracks.BaseUrl);

    /// <summary>OwnTracks Recorder connection settings.</summary>
    public sealed class OwnTracksOptions
    {
        /// <summary>Recorder base URL (e.g. <c>http://owntracks-recorder:8083</c>). Empty disables location.</summary>
        public string? BaseUrl { get; set; }

        /// <summary>Recorder user name the phone publishes as.</summary>
        public string? User { get; set; }

        /// <summary>Optional device id; when empty the newest fix across the user's devices wins.</summary>
        public string? Device { get; set; }
    }
}
