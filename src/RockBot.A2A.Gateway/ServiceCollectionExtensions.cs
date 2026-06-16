using A2A;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

        services.AddAuthorization();
        return services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, null);
    }

    /// <summary>The forwarding policy scheme that routes each request to API key or Bearer.</summary>
    internal const string CombinedSchemeName = "A2A";

    /// <summary>
    /// Adds JWT/Bearer authentication as a second accepted scheme using generic OIDC
    /// (<see cref="JwtAuthOptions.Authority"/> + <see cref="JwtAuthOptions.Audience"/>,
    /// with signing-key discovery via the authority's <c>/.well-known/openid-configuration</c>).
    /// Installs a forwarding policy scheme as the default so each request is handled by exactly
    /// one scheme — Bearer when an <c>Authorization: Bearer</c> header is present, API key
    /// otherwise. This lets the gateway accept <em>either</em> credential without double-issuing
    /// the 401 challenge. No-ops when <see cref="JwtAuthOptions.IsEnabled"/> is false.
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

        // A policy scheme forwards each request to a single concrete scheme: Bearer when the
        // caller presents a bearer token, API key otherwise. A single scheme authenticates and
        // (on failure) challenges, avoiding the connection-resetting double-challenge that a
        // multi-scheme authorization policy would produce.
        builder.AddPolicyScheme(CombinedSchemeName, CombinedSchemeName, options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                string authorization = context.Request.Headers.Authorization.ToString();
                return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? JwtBearerDefaults.AuthenticationScheme
                    : ApiKeyAuthenticationHandler.SchemeName;
            };
        });

        // Make the policy scheme the default so the (unchanged) RequireAuthenticatedUser policy
        // authenticates and challenges through the forwarding selector.
        builder.Services.Configure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = CombinedSchemeName;
            options.DefaultAuthenticateScheme = CombinedSchemeName;
            options.DefaultChallengeScheme = CombinedSchemeName;
        });

        return builder;
    }
}
