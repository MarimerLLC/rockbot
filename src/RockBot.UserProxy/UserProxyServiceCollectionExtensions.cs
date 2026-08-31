using Microsoft.Extensions.DependencyInjection;

namespace RockBot.UserProxy;

/// <summary>
/// DI registration extensions for the user proxy service.
/// </summary>
public static class UserProxyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the user proxy service as a hosted service with optional configuration.
    /// </summary>
    public static IServiceCollection AddUserProxy(
        this IServiceCollection services,
        Action<UserProxyOptions>? configure = null)
    {
        var options = new UserProxyOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddSingleton<UserProxyService>();
        services.AddHostedService(sp => sp.GetRequiredService<UserProxyService>());

        return services;
    }
}
