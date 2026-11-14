using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace RockBot.UserProxy.AgUi;

/// <summary>
/// Who a request is from, as far as the rest of the proxy needs to know: the user id it sends to
/// the agent, and when the caller's credential runs out.
/// </summary>
internal static class AgUiIdentity
{
    /// <summary>
    /// The <see cref="UserMessage.UserId"/> for a request: a claim of the authenticated caller in
    /// app mode, <see cref="AgUiOptions.DefaultUserId"/> otherwise.
    /// </summary>
    /// <remarks>
    /// The agent does not key anything on the user id: there is one user as far as it is
    /// concerned, and everyone the proxy lets in shares that identity, its history and its memory
    /// (multi-tenancy is a non-goal). The id is written to the agent's log line for the message
    /// and to the turn's telemetry (<c>rockbot.user.id</c>), so what it is for is telling an
    /// operator who sent something. That is the email address: it is what the allowlist is
    /// written in and what an operator recognises, where <c>sub</c> is an opaque number.
    /// <see cref="AgUiAuthOptions.UserIdClaim"/> can name <c>sub</c> for a deployment that doesn't
    /// want addresses in its telemetry.
    /// </remarks>
    public static string UserId(ClaimsPrincipal? user, AgUiOptions options)
    {
        if (!options.Auth.Enabled || user?.Identity?.IsAuthenticated != true)
            return options.DefaultUserId;

        var claim = options.Auth.UserIdClaim;
        var value = string.Equals(claim, "email", StringComparison.OrdinalIgnoreCase)
            ? UserAllowlist.GetEmail(user)?.Trim().ToLowerInvariant()
            : user.FindFirst(claim)?.Value;

        if (string.IsNullOrWhiteSpace(value))
            value = user.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(value) ? options.DefaultUserId : value;
    }

    /// <summary>When the caller's token expires (its <c>exp</c> claim), or null when there is none: network mode.</summary>
    public static DateTimeOffset? Expires(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true
        && long.TryParse(user.FindFirst("exp")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}

/// <summary>Satisfied only by a caller on the proxy's allowlist.</summary>
internal sealed class AgUiAllowlistRequirement : IAuthorizationRequirement;

/// <summary>
/// Checks <see cref="AgUiAllowlistRequirement"/> against the shared <see cref="UserAllowlist"/>,
/// on every request: a token is proof of who the caller is, never of being allowed in.
/// </summary>
internal sealed class AgUiAllowlistHandler(UserAllowlist allowlist, AgUiOptions options, ILoggerFactory loggers)
    : AuthorizationHandler<AgUiAllowlistRequirement>
{
    private readonly ILogger _logger = loggers.CreateLogger(AgUiAuth.LogCategory);

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AgUiAllowlistRequirement requirement)
    {
        var user = context.User;
        if (allowlist.IsAllowed(user) && (!options.Auth.RequireVerifiedEmail || HasVerifiedEmail(user)))
        {
            context.Succeed(requirement);
        }
        else if (user.Identity?.IsAuthenticated == true)
        {
            // The address goes to the log, where an operator who has to extend the allowlist
            // needs it, and never into the response.
            _logger.LogWarning("Refused {Email}: not on the allowlist, or the address is not verified",
                UserAllowlist.GetEmail(user) ?? "(no email claim)");
        }
        return Task.CompletedTask;
    }

    /// <summary>True only when the token says <c>email_verified: true</c>. Absent is not verified.</summary>
    internal static bool HasVerifiedEmail(ClaimsPrincipal user) =>
        bool.TryParse(user.FindFirst("email_verified")?.Value, out var verified) && verified;
}

/// <summary>
/// Authenticates a request by the access ticket in its URL (see <see cref="AccessTicketService"/>).
/// A ticket authenticates only the request it was issued for: a <c>GET</c> of that attachment or
/// that thread's notify stream. Anywhere else it is not a credential at all.
/// </summary>
internal sealed class AccessTicketHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions, ILoggerFactory loggers, UrlEncoder encoder,
    AccessTicketService tickets)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggers, encoder)
{
    public const string SchemeName = "AccessTicket";

    /// <summary>The path prefix of a ticketed attachment URL: <c>/t/{ticket}/attachments/{path}</c>.</summary>
    public const string PathPrefix = "/t/";

    public const string NotifyPath = "/agui/notify";

    public const string QueryParameter = "ticket";

    private string? _failure;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryFindTicket(Request, out var ticket, out var attachmentPath))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!HttpMethods.IsGet(Request.Method))
            return Task.FromResult(Fail("An access ticket is only good for GET."));

        if (tickets.Validate(ticket) is not { } grant)
            return Task.FromResult(Fail("The access ticket is not valid or has expired."));

        var covered = attachmentPath is not null
            ? grant.CoversAttachment(attachmentPath)
            : grant.CoversNotify(Request.Query["threadId"].ToString());
        if (!covered)
            return Task.FromResult(Fail("The access ticket is not for this resource."));

        // The endpoint checks the grant again, against the path or thread it actually serves:
        // this handler read them from the raw request, and the two must not be able to differ.
        Context.Items[typeof(AccessGrant)] = grant;

        // The claims the allowlist check reads. The address was verified when the ticket was
        // issued, from the bearer token; exp is that token's, so a stream ends when it would have.
        var identity = new ClaimsIdentity(
            [
                new Claim("email", grant.Email),
                new Claim("email_verified", "true"),
                new Claim("exp", grant.TokenExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ],
            SchemeName, "email", null);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <summary>
    /// The grant of the ticket a request was authenticated by, or null when it wasn't by a
    /// ticket (a bearer token, or network mode), in which case nothing narrows what it may fetch.
    /// </summary>
    public static AccessGrant? GrantOf(HttpContext context) =>
        context.User.Identity?.AuthenticationType == SchemeName
            // Authenticated by a ticket and no grant on record can't happen; if it does, a grant
            // that covers nothing.
            ? context.Items[typeof(AccessGrant)] as AccessGrant
              ?? new AccessGrant(AccessTicketKind.Attachment, "", "", DateTimeOffset.MinValue, DateTimeOffset.MinValue)
            : null;

    private AuthenticateResult Fail(string why)
    {
        _failure = why;
        return AuthenticateResult.Fail(why);
    }

    /// <summary>
    /// The ticket a request carries, if any: in the path of a ticketed attachment URL (then
    /// <paramref name="attachmentPath"/> is the attachment asked for), or in the query of the
    /// notify stream. A <c>ticket</c> parameter anywhere else is ignored.
    /// </summary>
    internal static bool TryFindTicket(HttpRequest request, out string ticket, out string? attachmentPath)
    {
        ticket = "";
        attachmentPath = null;
        var path = request.Path.Value ?? "";

        if (path.StartsWith(PathPrefix, StringComparison.Ordinal))
        {
            var end = path.IndexOf('/', PathPrefix.Length);
            const string attachments = AttachmentLinks.Route + "/";
            if (end < 0 || string.CompareOrdinal(path, end, attachments, 0, attachments.Length) != 0)
                return false;

            ticket = path[PathPrefix.Length..end];
            attachmentPath = path[(end + attachments.Length)..];
            return ticket.Length > 0;
        }

        if (string.Equals(path, NotifyPath, StringComparison.Ordinal)
            && request.Query[QueryParameter].ToString() is { Length: > 0 } inQuery)
        {
            ticket = inQuery;
            return true;
        }

        return false;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        AgUiAuth.ChallengeAsync(Context, _failure is null ? null : "invalid_token", _failure);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => AgUiAuth.ForbidAsync(Context);
}

/// <summary>Marks an endpoint a client calls: a request to it that names an <c>Origin</c> must name an allowed one.</summary>
internal sealed class OriginCheckedMetadata;

/// <summary>
/// Decides whether a request that carries an <c>Origin</c> header may reach a client endpoint.
/// In both modes: it is what keeps a web page on some other site, open in a browser that can
/// reach the proxy, from sending it requests.
/// </summary>
/// <remarks>
/// <para>
/// CORS only stops a page from <em>reading</em> a response. A "simple" request (a form post, a
/// <c>text/plain</c> or multipart body, a POST with no body) is sent without a preflight, and in
/// network mode nothing else asks who is calling. So a page on any site could start a run, cancel
/// one, clear the context or upload a file, blind. Browsers put <c>Origin</c> on every request
/// that isn't a same-origin or no-CORS <c>GET</c>/<c>HEAD</c>, and a page can't forge or remove
/// it, which makes it the thing to check.
/// </para>
/// <para>
/// Allowed: the configured CORS origins, and the proxy's own origin. Its own origin is
/// <see cref="AgUiOptions.PublicBaseUrl"/>'s when that is set, and otherwise any origin whose
/// host and port are the request's <c>Host</c>. The scheme is not compared in that second case:
/// behind something that terminates TLS the proxy sees <c>http</c> where the browser said
/// <c>https</c>. <c>Origin: null</c> (a sandboxed document, such as an HTML attachment) is never
/// allowed. A request with no <c>Origin</c> is not a cross-site browser request and passes:
/// <c>curl</c>, a phone app, a server, a navigation, an <c>&lt;img&gt;</c>.
/// </para>
/// </remarks>
internal sealed class OriginCheck
{
    private readonly HashSet<string> _allowed;

    public OriginCheck(AgUiOptions options)
    {
        _allowed = new HashSet<string>(options.Cors.ResolveOrigins(), StringComparer.OrdinalIgnoreCase);
        if (options.ResolvePublicBaseUrl() is { } publicBaseUrl)
            _allowed.Add(new Uri(publicBaseUrl).GetLeftPart(UriPartial.Authority));
    }

    /// <summary>True when the request names no origin, or one that may call the API.</summary>
    public bool Allows(HttpRequest request)
    {
        var origins = request.Headers.Origin;
        if (origins.Count == 0)
            return true;
        if (origins.Count != 1 || origins[0] is not { Length: > 0 } origin)
            return false;
        if (_allowed.Contains(origin))
            return true;

        // Same origin, as far as the proxy can tell: the page was served from the address this
        // request was sent to.
        var scheme = origin.IndexOf("://", StringComparison.Ordinal);
        return scheme > 0
            && request.Host.HasValue
            && string.Equals(origin[(scheme + 3)..], request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Wires app auth, CORS and the origin check into the proxy (#794, phase 5). With
/// <see cref="AgUiAuthOptions.Enabled"/> off and no CORS origins, no auth or CORS services are
/// registered and neither middleware is added; the origin check is always on.
/// </summary>
internal static class AgUiAuth
{
    public const string LogCategory = "RockBot.UserProxy.AgUi.Auth";

    /// <summary>The scheme every request is authenticated by: it hands over to bearer or ticket.</summary>
    public const string SchemeName = "AgUi";

    public const string CorsPolicy = "AgUi";

    /// <summary>
    /// Validates the auth and CORS configuration (throwing on an unusable one, so the proxy
    /// doesn't start half-open) and registers what the enabled parts need.
    /// </summary>
    public static IServiceCollection AddAgUiAccess(this IServiceCollection services, AgUiOptions options)
    {
        var auth = options.Auth;
        var problems = auth.Validate().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

        var origins = options.Cors.ResolveOrigins();
        if (origins.Count > 0)
        {
            services.AddCors(cors => cors.AddPolicy(CorsPolicy, policy => policy
                .WithOrigins([.. origins])
                .WithMethods("GET", "POST", "DELETE")
                // Any request header. The allow-list of headers protects an API that acts on
                // something the browser adds by itself (a cookie); this one acts only on what the
                // calling script puts in Authorization, and never allows credentials. Naming
                // headers one by one would only break the next client library that adds its own.
                .AllowAnyHeader()
                // What a script may read besides the safelisted headers: where an upload or a
                // saved response went, and why a request was refused.
                .WithExposedHeaders("Location", "WWW-Authenticate")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
        }

        services.AddSingleton(new OriginCheck(options));
        services.AddSingleton(sp => new AccessTicketService(auth.ResolveTicketKey(), sp.GetService<TimeProvider>()));
        if (!auth.Enabled)
            return services;

        services.AddSingleton(new UserAllowlist(auth));
        services.AddSingleton<IAuthorizationHandler, AgUiAllowlistHandler>();

        services.AddAuthentication(SchemeName)
            .AddPolicyScheme(SchemeName, null, scheme =>
            {
                // A request with a bearer token is judged by that token alone, even if its URL
                // also has a ticket. Without one, a ticket is the only other way in.
                scheme.ForwardDefaultSelector = context =>
                    context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? JwtBearerDefaults.AuthenticationScheme
                        : AccessTicketHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, AccessTicketHandler>(AccessTicketHandler.SchemeName, null)
            .AddJwtBearer(bearer =>
            {
                // Issuer and signing keys come from the authority's discovery document.
                bearer.Authority = auth.Authority.Trim();
                bearer.RequireHttpsMetadata = true;
                // Keep the token's own claim names (email, email_verified, sub, exp).
                bearer.MapInboundClaims = false;
                bearer.SaveToken = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    // Added to the discovery document's issuer, which is always accepted.
                    ValidIssuers = auth.ResolveExtraIssuers() is { Count: > 0 } issuers ? issuers : null,
                    ValidateAudience = true,
                    ValidAudiences = auth.ResolveAudiences(),
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    // Asymmetric only: the keys are the issuer's published public keys, and a
                    // token "signed" with one of those as an HMAC secret must not verify.
                    ValidAlgorithms =
                    [
                        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
                        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
                        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
                    ],
                    ClockSkew = auth.ClockSkew,
                    NameClaimType = "email",
                };
                bearer.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        // The reason goes to the log; the response only says the token is no good.
                        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory)
                            .LogInformation("Refused a bearer token: {Reason}", context.Exception.Message);
                        return Task.CompletedTask;
                    },
                    OnChallenge = context =>
                    {
                        context.HandleResponse();
                        return context.AuthenticateFailure switch
                        {
                            null => ChallengeAsync(context.HttpContext, null, null),
                            SecurityTokenExpiredException => ChallengeAsync(context.HttpContext, "invalid_token", "The token has expired."),
                            _ => ChallengeAsync(context.HttpContext, "invalid_token", "The token is not valid."),
                        };
                    },
                    OnForbidden = context => ForbidAsync(context.HttpContext),
                };
            });

        services.AddAuthorization(authorization =>
        {
            authorization.DefaultPolicy = new AuthorizationPolicyBuilder(SchemeName)
                .RequireAuthenticatedUser()
                .AddRequirements(new AgUiAllowlistRequirement())
                .Build();
        });

        return services;
    }

    /// <summary>Adds the middleware the enabled parts need: CORS first, so a refusal carries its headers too.</summary>
    public static WebApplication UseAgUiAccess(this WebApplication app, AgUiOptions options)
    {
        if (options.Cors.ResolveOrigins().Count > 0)
            app.UseCors(CorsPolicy);

        // After CORS, which answers preflights by itself, and before authentication and the
        // endpoint: a request from an origin that isn't allowed is refused before anything
        // looks at it, in both modes. Only client endpoints are checked; /healthz and the
        // sign-in redirect are not.
        var originCheck = app.Services.GetRequiredService<OriginCheck>();
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<OriginCheckedMetadata>() is not null
                && !originCheck.Allows(context.Request))
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory)
                    .LogInformation("Refused {Method} {Path} from origin {Origin}: not an allowed origin",
                        context.Request.Method, context.Request.Path, context.Request.Headers.Origin.ToString());
                await AgUiEndpoints.Refusal(StatusCodes.Status403Forbidden,
                        "This origin is not allowed to call this API. A browser app on another origin than the proxy's "
                        + "has to be listed in AgUi:Cors:AllowedOrigins.")
                    .ExecuteAsync(context);
                return;
            }
            await next(context);
        });

        if (options.Auth.Enabled)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        return app;
    }

    /// <summary>
    /// <c>401</c>: no credential, or one that is no good. <c>WWW-Authenticate: Bearer</c> says how
    /// to get in, with RFC 6750's <c>error</c> when a credential was sent.
    /// </summary>
    public static Task ChallengeAsync(HttpContext context, string? error, string? description)
    {
        if (context.Response.HasStarted)
            return Task.CompletedTask;

        context.Response.Headers.WWWAuthenticate = error is null
            ? "Bearer"
            : $"Bearer error=\"{error}\", error_description=\"{description}\"";
        return AgUiEndpoints.Refusal(StatusCodes.Status401Unauthorized,
                description ?? "Sign-in is required. Send an OIDC ID token in the Authorization header: Bearer <token>.")
            .ExecuteAsync(context);
    }

    /// <summary>
    /// <c>403</c>: the caller is who the token says, and isn't allowed in. The body never says
    /// who is: not the address that was refused, and nothing about the allowlist.
    /// </summary>
    public static Task ForbidAsync(HttpContext context) =>
        context.Response.HasStarted
            ? Task.CompletedTask
            : AgUiEndpoints.Refusal(StatusCodes.Status403Forbidden, "This account is not allowed to use this API.")
                .ExecuteAsync(context);
}
