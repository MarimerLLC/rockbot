using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// The user's latest known location, written by <see cref="UserLocationService"/> and read when
/// building each turn's datetime line. Registered only when location is enabled, so consumers
/// treat its presence as "location is on".
/// </summary>
public sealed class UserLocationState(IOptions<UserLocationOptions> options)
{
    private volatile UserLocation? _current;

    /// <summary>The latest fresh fix, or <c>null</c> when there is none.</summary>
    public UserLocation? Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>Fixes older than this are treated as unknown.</summary>
    public TimeSpan MaxAge { get; } = options.Value.MaxAge;
}
