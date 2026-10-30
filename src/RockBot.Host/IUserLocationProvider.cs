namespace RockBot.Host;

/// <summary>
/// A source of the user's latest reported location (e.g. an OwnTracks Recorder).
/// </summary>
public interface IUserLocationProvider
{
    /// <summary>
    /// Returns the newest fix the source holds, or <c>null</c> when it has none.
    /// Throws when the source can't be reached.
    /// </summary>
    Task<UserLocation?> GetLatestAsync(CancellationToken cancellationToken);
}
