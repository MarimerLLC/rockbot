namespace RockBot.Host;

/// <summary>
/// The user's most recent reported position, as supplied by an <see cref="IUserLocationProvider"/>.
/// </summary>
/// <param name="Latitude">Latitude in decimal degrees.</param>
/// <param name="Longitude">Longitude in decimal degrees.</param>
/// <param name="AccuracyMeters">Reported horizontal accuracy, when the source supplies one.</param>
/// <param name="ReportedAt">When the device took the fix.</param>
/// <param name="TimeZoneId">IANA timezone at the fix (e.g. "Europe/Amsterdam"), when known.</param>
/// <param name="Locality">City or town name, when the source has reverse-geocoded the fix.</param>
/// <param name="CountryCode">ISO country code, when the source has reverse-geocoded the fix.</param>
public sealed record UserLocation(
    double Latitude,
    double Longitude,
    double? AccuracyMeters,
    DateTimeOffset ReportedAt,
    string? TimeZoneId = null,
    string? Locality = null,
    string? CountryCode = null);
