using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace RockBot.UserProxy.WorkIqAuth;

/// <summary>
/// DI helpers for the UI-tier WorkIQ flow.
/// </summary>
public static class WorkIqAuthClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WorkIqClientSettings"/>, <see cref="WorkIqDeviceCodeFlow"/>,
    /// and the <see cref="WorkIqAuthStatusListener"/> hosted service. Bind settings
    /// from the <c>WorkIQ</c> configuration section.
    /// </summary>
    public static IServiceCollection AddWorkIqAuthClient(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkIqClientSettings>(opts =>
        {
            var section = configuration.GetSection("WorkIQ");
            opts.TenantId = section["TenantId"] ?? string.Empty;
            opts.ClientId = section["ClientId"] ?? string.Empty;
            opts.Authority = section["Authority"];

            var scopesValue = section["Scopes"];
            if (!string.IsNullOrWhiteSpace(scopesValue))
            {
                opts.Scopes = scopesValue
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
        });

        services.AddSingleton<WorkIqDeviceCodeFlow>();

        // The listener is registered as a singleton AND a hosted service. UI
        // components inject IWorkIqAuthStatusListener; the hosted service
        // role drives subscribe/unsubscribe via StartAsync/StopAsync.
        services.AddSingleton<WorkIqAuthStatusListener>();
        services.TryAddSingleton<IWorkIqAuthStatusListener>(
            sp => sp.GetRequiredService<WorkIqAuthStatusListener>());
        services.AddHostedService(sp => sp.GetRequiredService<WorkIqAuthStatusListener>());

        return services;
    }
}
