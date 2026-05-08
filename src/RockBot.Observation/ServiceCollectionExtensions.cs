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
        services.TryAddSingleton<IObservationExtractor, LlmObservationExtractor>();
        services.TryAddSingleton<IObservationExtractionPhase, ObservationExtractionPhase>();
        services.TryAddSingleton<IObservationEvaluator, LlmObservationEvaluator>();
        services.TryAddSingleton<IObservationEvaluationPhase, ObservationEvaluationPhase>();
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

    /// <summary>
    /// Registers the framework's two default targets — theory-of-self and
    /// theory-of-user — rooted at the given agent profile directory. The
    /// targets use the built-in <see cref="TranscriptFilters"/> and
    /// <see cref="DefaultPrompts"/>. Hosts that want different prompts or
    /// filters should construct <see cref="ObservationTarget"/> instances
    /// directly and register them with <see cref="AddObservationTarget"/>.
    /// </summary>
    public static IServiceCollection AddDefaultObservationTargets(
        this IServiceCollection services,
        string agentProfileBasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentProfileBasePath);

        services.AddObservationTarget(ObservationDefaults.CreateTheoryOfSelf(agentProfileBasePath));
        services.AddObservationTarget(ObservationDefaults.CreateTheoryOfUser(agentProfileBasePath));
        return services;
    }
}
