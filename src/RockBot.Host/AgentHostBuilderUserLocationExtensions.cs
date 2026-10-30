using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace RockBot.Host;

/// <summary>
/// Extension methods for following the user's location.
/// </summary>
public static class AgentHostBuilderUserLocationExtensions
{
    /// <summary>
    /// Follows the user's location: polls an OwnTracks Recorder, moves <see cref="AgentClock.Zone"/>
    /// to the user's current timezone, and adds their coarse whereabouts to each turn's datetime line.
    /// </summary>
    /// <remarks>
    /// When the configured <see cref="UserLocationOptions.OwnTracksOptions.BaseUrl"/> is empty this
    /// registers nothing, so the agent behaves exactly as it does without the call: consumers see no
    /// <see cref="UserLocationState"/> and take their location-free path.
    /// </remarks>
    public static AgentHostBuilder WithUserLocation(
        this AgentHostBuilder builder,
        Action<UserLocationOptions> configure)
    {
        var probe = new UserLocationOptions();
        configure(probe);
        if (!probe.IsEnabled)
            return builder;

        builder.Services.Configure(configure);
        builder.Services.AddSingleton<UserLocationState>();
        builder.Services.TryAddSingleton<IUserLocationProvider, OwnTracksRecorderLocationProvider>();
        builder.Services.AddSingleton<IHostedService, UserLocationService>();

        return builder;
    }
}
