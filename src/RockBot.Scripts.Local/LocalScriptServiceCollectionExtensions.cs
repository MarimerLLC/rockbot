using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace RockBot.Scripts.Local;

/// <summary>
/// DI registration extensions for process-based local script execution.
/// </summary>
public static class LocalScriptServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IScriptRunner"/> backed by child Python processes on the local host.
    /// No container infrastructure is required; Python must be available in the host's PATH
    /// (or configured via <see cref="LocalScriptOptions.PythonExecutable"/>).
    /// </summary>
    /// <remarks>
    /// See <c>design/script-isolation-alternatives.md</c> for a full discussion of the
    /// isolation trade-offs of this approach vs. Kubernetes or Docker.
    /// </remarks>
    public static IServiceCollection AddLocalScriptRunner(
        this IServiceCollection services,
        Action<LocalScriptOptions>? configure = null)
    {
        var options = new LocalScriptOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.TryAddSingleton<IScriptRunner>(sp =>
            new LocalScriptRunner(
                sp.GetRequiredService<LocalScriptOptions>(),
                sp.GetRequiredService<ILogger<LocalScriptRunner>>()));

        return services;
    }
}
