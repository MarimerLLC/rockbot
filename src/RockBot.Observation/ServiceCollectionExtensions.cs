using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RockBot.Observation;

/// <summary>
/// DI registration helpers for the observation framework.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the framework's core services (state store) and ensures the
    /// configured target list is available via DI. Targets themselves are
    /// added with <see cref="AddObservationTarget"/>.
    /// </summary>
    public static IServiceCollection AddRockBotObservation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IObservationStateStore, FileObservationStateStore>();
        return services;
    }

    /// <summary>
    /// Registers a single <see cref="ObservationTarget"/> with the framework.
    /// Multiple targets can be registered; the pipeline iterates all of them
    /// during a dream cycle.
    /// </summary>
    public static IServiceCollection AddObservationTarget(
        this IServiceCollection services,
        ObservationTarget target)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(target);

        services.AddSingleton(target);
        return services;
    }
}
