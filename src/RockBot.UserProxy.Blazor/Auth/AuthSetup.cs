using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;

namespace RockBot.UserProxy.Blazor.Auth;

/// <summary>
/// Wires OAuth sign-in into the Blazor host: options binding and validation, the cookie and
/// provider handlers, the authorization policy, the middleware order, and the <c>/auth/*</c>
/// endpoints.
/// </summary>
public static class AuthSetup
{
    /// <summary>
    /// Binds and validates the <c>Auth</c> section, then registers everything sign-in needs.
    /// Returns the bound options so the caller can shape the rest of the pipeline.
    /// </summary>
    /// <remarks>
    /// Authorization services are registered whether or not sign-in is enabled, with a permissive
    /// default policy when it is off. That keeps one code path: <c>[Authorize]</c> and
    /// <c>.RequireAuthorization()</c> stay on the endpoints unconditionally, and turning
    /// <c>Auth:Enabled</c> off makes them pass rather than making them disappear — there is no
    /// second, untested arrangement of the pipeline to get wrong.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The configuration is enabled but unusable.</exception>
    public static AuthOptions AddRockBotAuth(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AuthOptions();
        configuration.GetSection(AuthOptions.SectionName).Bind(options);

        var problems = options.Validate().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

        var registry = new AuthProviderRegistry(options);
        var allowlist = new UserAllowlist(options);

        services.AddSingleton(options);
        services.AddSingleton(registry);
        services.AddSingleton(allowlist);

        if (options.Enabled)
        {
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(cookie =>
                {
                    cookie.Cookie.Name = "rockbot.auth";
                    cookie.Cookie.HttpOnly = true;
                    // Lax, not Strict: the provider redirects back with a cross-site GET, and a
                    // Strict cookie would not be sent on it.
                    cookie.Cookie.SameSite = SameSiteMode.Lax;
                    cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    cookie.LoginPath = "/login";
                    cookie.AccessDeniedPath = "/access-denied";
                    cookie.ReturnUrlParameter = "returnUrl";
                    cookie.ExpireTimeSpan = options.SessionLifetime;
                    cookie.SlidingExpiration = true;
                })
                .AddConfiguredProviders(options, registry);

            services.AddScoped<IAuthorizationHandler, AllowlistAuthorizationHandler>();
            services.AddScoped<AuthenticationStateProvider, RevalidatingAuthStateProvider>();
        }

        services.AddCascadingAuthenticationState();
        services.AddAuthorization(authz =>
        {
            authz.DefaultPolicy = options.Enabled
                ? new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new AllowlistRequirement())
                    .Build()
                // Sign-in is off: the network in front of the app is the gate, exactly as it was
                // before sign-in existed. Succeed for everyone, anonymous included.
                : new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();

            authz.AddPolicy(AllowlistRequirement.PolicyName, authz.DefaultPolicy);
        });

        return options;
    }

    /// <summary>
    /// Registers a handler per configured provider. Only providers the registry considers enabled
    /// are registered, so <c>/auth/challenge</c> can never name a scheme that does not exist.
    /// </summary>
    private static AuthenticationBuilder AddConfiguredProviders(
        this AuthenticationBuilder builder,
        AuthOptions options,
        AuthProviderRegistry registry)
    {
        foreach (var provider in registry.Enabled)
        {
            var credentials = options.Providers[provider.Key];

            switch (provider.Scheme)
            {
                case AuthProviderRegistry.GoogleScheme:
                    builder.AddGoogle(google =>
                    {
                        google.ClientId = credentials.ClientId;
                        google.ClientSecret = credentials.ClientSecret;
                        // This is the value that goes into the Google Cloud Console, verbatim:
                        // https://{host}/signin-google
                        google.CallbackPath = "/signin-google";
                        google.SaveTokens = false;   // Nothing here calls a Google API on the user's behalf.

                        // Google publishes email_verified, but the handler does not map it by
                        // default. Without this the allowlist's verification check has no claim to
                        // read and silently degrades to trusting whatever address came back.
                        google.ClaimActions.MapJsonKey("email_verified", "email_verified", ClaimValueTypes.Boolean);
                    });
                    break;

                default:
                    throw new InvalidOperationException(
                        $"No handler registration exists for provider scheme '{provider.Scheme}'.");
            }
        }

        return builder;
    }

    /// <summary>
    /// Pins the request's external identity from <see cref="AuthOptions.PublicBaseUrl"/>, or trusts
    /// forwarded headers when the operator has opted in. Must run before anything that builds an
    /// absolute URL — HTTPS redirection and, critically, the OAuth <c>redirect_uri</c>.
    /// </summary>
    /// <remarks>
    /// This is the failure mode the feature would otherwise ship with. Behind a TLS-terminating
    /// ingress the app still sees plain http on :8080, so ASP.NET Core builds an <c>http://</c>
    /// callback; Google accepts https for everything except <c>http://localhost</c>, so it rejects
    /// that with <c>redirect_uri_mismatch</c> and no hint as to why.
    /// </remarks>
    public static IApplicationBuilder UseRockBotPublicOrigin(this IApplicationBuilder app, AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        if (options.TrustForwardedHeaders)
        {
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
                // Cleared deliberately, and only because the operator opted in: the defaults accept
                // forwarded headers from loopback only, which an in-cluster ingress controller is
                // not. Trusting these headers from anywhere is why this is off by default.
                KnownIPNetworks = { },
                KnownProxies = { },
            });
        }

        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
            return app;

        if (!Uri.TryCreate(options.PublicBaseUrl.Trim(), UriKind.Absolute, out var publicUri))
        {
            throw new InvalidOperationException(
                $"Auth:PublicBaseUrl ('{options.PublicBaseUrl}') is not an absolute URL. " +
                "Use the full external address, e.g. https://rockbot.example.com.");
        }

        var scheme = publicUri.Scheme;
        var host = new HostString(publicUri.IsDefaultPort ? publicUri.Host : $"{publicUri.Host}:{publicUri.Port}");
        var pathBase = publicUri.AbsolutePath.TrimEnd('/');

        // Overriding the request rather than only the callback URI means every absolute URL the app
        // builds agrees with the address the browser actually used — and it needs no trust in any
        // header, which is what makes this the setting to reach for first.
        return app.Use(async (context, next) =>
        {
            context.Request.Scheme = scheme;
            context.Request.Host = host;
            if (pathBase.Length > 0)
                context.Request.PathBase = pathBase;
            await next();
        });
    }

    /// <summary>
    /// Maps the sign-in transitions. They are minimal-API endpoints rather than component code
    /// because <c>SignInAsync</c> writes response headers: by the time an interactive component
    /// renders over its SignalR circuit, the HTTP response that created it is long gone.
    /// </summary>
    public static IEndpointRouteBuilder MapRockBotAuthEndpoints(
        this IEndpointRouteBuilder endpoints,
        AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        // Anonymous and cheap. The probes previously hit "/", which with sign-in on answers 302 to
        // /login — still scored as success by the kubelet, so the probe would pass without
        // asserting anything. Mapped whether or not sign-in is enabled so the chart has one path.
        endpoints.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

        if (!options.Enabled)
            return endpoints;

        endpoints.MapGet("/auth/challenge", (
            string? provider,
            string? returnUrl,
            AuthProviderRegistry registry) =>
        {
            var resolved = registry.Resolve(provider);
            if (resolved is null)
            {
                // Unknown or unconfigured: back to the chooser, rather than a 500 from challenging
                // a scheme that was never registered.
                return Results.Redirect("/login");
            }

            var properties = new AuthenticationProperties
            {
                // Sanitized on the way in, not at the callback: the provider echoes this value back
                // through the state parameter, and it is where an open redirect would live — worse
                // than usual here, because the victim has just authenticated.
                RedirectUri = LocalReturnUrl.Sanitize(returnUrl),
                // The half of "stay signed in" that lives in the browser. Without it the cookie is a
                // session cookie and closing the browser signs you out, however carefully the key
                // ring is persisted on the server.
                IsPersistent = true,
            };

            return Results.Challenge(properties, [resolved.Scheme]);
        }).AllowAnonymous();

        // Anonymous on purpose. Someone signed in with an account that is NOT on the allowlist
        // fails the default policy, so requiring authorization here would leave them unable to sign
        // out and switch accounts — bounced between /access-denied and a 403 with no way forward.
        // Signing out is safe for any caller; the antiforgery middleware still requires a token,
        // so a third-party site cannot log a user out.
        endpoints.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).AllowAnonymous();

        return endpoints;
    }
}
