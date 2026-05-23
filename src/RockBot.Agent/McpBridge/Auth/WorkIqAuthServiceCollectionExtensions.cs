using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using RockBot.Tools.Mcp.Auth;

namespace RockBot.Agent.McpBridge.Auth;

/// <summary>
/// Service collection helpers for the WorkIQ auth profile.
/// </summary>
public static class WorkIqAuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MSAL public-client app, the WorkIQ <see cref="ITokenProvider"/>,
    /// the cache-syncing hosted service, and binds <see cref="MsalTokenProviderOptions"/>
    /// from the <c>WorkIQ</c> configuration section.
    /// </summary>
    /// <remarks>
    /// Call sites should gate this registration on <c>WorkIQ:TenantId</c> being
    /// non-empty so deployments without WorkIQ avoid the MSAL dependency at runtime.
    /// </remarks>
    public static IServiceCollection AddWorkIqAuth(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MsalTokenProviderOptions>(opts =>
        {
            var section = configuration.GetSection("WorkIQ");
            opts.TenantId = section["TenantId"] ?? string.Empty;
            opts.ClientId = section["ClientId"] ?? string.Empty;
            opts.Authority = section["Authority"];
            opts.CacheFilePath = section["CacheFilePath"]
                ?? "/data/agent/secrets/workiq-cache.bin";

            var scopesValue = section["Scopes"];
            if (!string.IsNullOrWhiteSpace(scopesValue))
            {
                opts.Scopes = scopesValue
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
        });

        services.AddSingleton<IPublicClientApplication>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MsalTokenProviderOptions>>().Value;
            var builder = PublicClientApplicationBuilder.Create(opts.ClientId);
            if (!string.IsNullOrWhiteSpace(opts.Authority))
                builder = builder.WithAuthority(opts.Authority);
            else if (!string.IsNullOrWhiteSpace(opts.TenantId))
                builder = builder.WithAuthority(AzureCloudInstance.AzurePublic, opts.TenantId);
            return builder.Build();
        });

        services.AddSingleton<MsalTokenProvider>();
        services.AddSingleton<TokenProviderRegistration>(sp =>
            new TokenProviderRegistration("workiq", sp.GetRequiredService<MsalTokenProvider>()));
        services.AddHostedService<TokenCacheStore>();

        // Register the registry only once even if AddWorkIqAuth is paired with
        // future providers. TryAdd ensures other providers can layer their
        // TokenProviderRegistration entries without re-registering the registry.
        services.AddSingleton<ITokenProviderRegistry, TokenProviderRegistry>();

        return services;
    }
}
