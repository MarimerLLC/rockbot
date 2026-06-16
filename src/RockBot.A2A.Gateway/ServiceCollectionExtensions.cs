using A2A;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using RockBot.A2A.Gateway.Auth;

namespace RockBot.A2A.Gateway;

/// <summary>
/// DI registration helpers for hosting an A2A HTTP gateway in an ASP.NET Core
/// application. Consumers call <see cref="AddA2AHttpGateway"/> to wire up the
/// A2A server, stores, and bridge handler; <see cref="AddA2AApiKeyAuthentication"/>
/// to register the API-key auth scheme; and then map endpoints via
/// <c>WebApplication.MapA2AHttpGateway()</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the A2A gateway services: task store, push-notification store,
    /// bridge handler, and <see cref="A2AServer"/>. The consumer must separately
    /// register a RockBot message publisher/subscriber (e.g. <c>AddRockBotRabbitMq</c>).
    /// </summary>
    public static IServiceCollection AddA2AHttpGateway(
        this IServiceCollection services,
        Action<GatewayOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddHttpContextAccessor();
        services.AddHttpClient();

        services.AddSingleton<ITaskStore>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value;
            var path = opts.TaskStorePath is not null
                ? Path.Combine(AppContext.BaseDirectory, opts.TaskStorePath)
                : null;
            return new FileTaskStore(sp.GetRequiredService<IHttpContextAccessor>(), path);
        });
        services.AddSingleton<ChannelEventNotifier>();
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value;
            var path = opts.PushNotificationConfigStorePath is not null
                ? Path.Combine(AppContext.BaseDirectory, opts.PushNotificationConfigStorePath)
                : null;
            return new FilePushNotificationConfigStore(path);
        });
        services.AddSingleton<PushNotificationSender>();
        services.AddSingleton<IAgentHandler, RockBotBridgeHandler>();
        services.AddSingleton(sp => new A2AServer(
            sp.GetRequiredService<IAgentHandler>(),
            sp.GetRequiredService<ITaskStore>(),
            sp.GetRequiredService<ChannelEventNotifier>(),
            sp.GetRequiredService<ILogger<A2AServer>>(),
            new A2AServerOptions()));

        return services;
    }

    /// <summary>
    /// Registers the <see cref="ApiKeyAuthenticationHandler"/> scheme and authorization
    /// services. Consumers still need to bind a <c>Dictionary&lt;string, ApiKeyEntry&gt;</c>
    /// from configuration (e.g. <c>Configure&lt;Dictionary&lt;string, ApiKeyEntry&gt;&gt;(cfg.GetSection("ApiKeys"))</c>).
    /// Call <see cref="AddA2AJwtBearerAuthentication"/> on the returned builder to add JWT/Bearer
    /// as a second accepted scheme.
    /// </summary>
    public static AuthenticationBuilder AddA2AApiKeyAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Default policy accepts the API-key scheme. AddA2AJwtBearerAuthentication widens this
        // to also accept Bearer when JWT auth is enabled.
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build());

        return services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, null);
    }

    /// <summary>
    /// Adds JWT/Bearer authentication as a second accepted scheme using generic OIDC
    /// (<see cref="JwtAuthOptions.Authority"/> + <see cref="JwtAuthOptions.Audience"/>,
    /// with signing-key discovery via the authority's <c>/.well-known/openid-configuration</c>).
    /// Widens the default authorization policy so the gateway accepts <em>either</em> API key
    /// or a valid Bearer token. No-ops when <see cref="JwtAuthOptions.IsEnabled"/> is false.
    /// </summary>
    public static AuthenticationBuilder AddA2AJwtBearerAuthentication(
        this AuthenticationBuilder builder, JwtAuthOptions jwtOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(jwtOptions);

        if (!jwtOptions.IsEnabled)
            return builder;

        builder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = jwtOptions.Authority;
            options.RequireHttpsMetadata = jwtOptions.RequireHttpsMetadata;
            if (!string.IsNullOrWhiteSpace(jwtOptions.Audience))
                options.Audience = jwtOptions.Audience;
            options.TokenValidationParameters.ValidateAudience =
                !string.IsNullOrWhiteSpace(jwtOptions.Audience);
        });

        // Re-build the default policy so RequireAuthorization() on POST / accepts either scheme.
        builder.Services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder(
                    ApiKeyAuthenticationHandler.SchemeName,
                    JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build());

        return builder;
    }
}
