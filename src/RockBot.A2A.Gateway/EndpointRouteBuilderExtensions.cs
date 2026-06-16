using A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.A2A.Gateway.Auth;

using A2AAgentCard = A2A.AgentCard;
using A2AAgentSkill = A2A.AgentSkill;

namespace RockBot.A2A.Gateway;

/// <summary>
/// Endpoint-mapping extensions for the A2A HTTP gateway. Adds the public
/// agent-card discovery endpoint and the authenticated JSON-RPC endpoint.
/// Middleware ordering (Authentication/Authorization) is the consumer's
/// responsibility.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the standard A2A endpoints: <c>GET /.well-known/agent-card.json</c> (anonymous)
    /// and <c>POST /</c> (requires authorization).
    /// </summary>
    public static IEndpointRouteBuilder MapA2AHttpGateway(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/.well-known/agent-card.json",
            (IOptions<GatewayOptions> opts, IOptions<JwtAuthOptions> jwtOpts) =>
                Results.Json(BuildAgentCard(opts.Value, jwtOpts.Value)));

        endpoints.MapPost("/", async (HttpContext ctx, A2AServer server,
            IOptions<GatewayOptions> opts, FilePushNotificationConfigStore pushConfigStore,
            ILoggerFactory loggerFactory) =>
        {
            var result = await JsonRpcRouter.HandleAsync(
                ctx.Request, ctx.Response, server, opts, pushConfigStore, loggerFactory, ctx.RequestAborted);
            if (result is not null)
                await result.ExecuteAsync(ctx);
        }).RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// Builds the published agent card. Advertises the <c>apiKey</c> security scheme always,
    /// plus <c>bearer</c> (HTTP bearer/JWT) and <c>openId</c> (OIDC discovery) schemes when
    /// JWT auth is enabled. Security requirements are listed separately so a caller satisfies
    /// the requirement with <em>either</em> scheme (requirements are OR-ed in A2A/OpenAPI).
    /// </summary>
    internal static A2AAgentCard BuildAgentCard(GatewayOptions config, JwtAuthOptions jwt)
    {
        var securitySchemes = new Dictionary<string, SecurityScheme>
        {
            ["apiKey"] = new SecurityScheme
            {
                ApiKeySecurityScheme = new ApiKeySecurityScheme
                {
                    Name = ApiKeyAuthenticationHandler.HeaderName,
                    Description = "API key for agent authentication",
                    Location = "header"
                }
            }
        };
        var securityRequirements = new List<SecurityRequirement>
        {
            new() { Schemes = new Dictionary<string, StringList> { ["apiKey"] = new StringList() } }
        };

        if (jwt.IsEnabled)
        {
            securitySchemes["bearer"] = new SecurityScheme
            {
                HttpAuthSecurityScheme = new HttpAuthSecurityScheme
                {
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "OAuth2/OIDC bearer token (JWT) for agent authentication"
                }
            };
            securitySchemes["openId"] = new SecurityScheme
            {
                OpenIdConnectSecurityScheme = new OpenIdConnectSecurityScheme
                {
                    OpenIdConnectUrl = $"{jwt.Authority!.TrimEnd('/')}/.well-known/openid-configuration",
                    Description = "OpenID Connect discovery document for the trusted authority"
                }
            };
            securityRequirements.Add(new SecurityRequirement
            {
                Schemes = new Dictionary<string, StringList> { ["bearer"] = new StringList() }
            });
        }

        return new A2AAgentCard
        {
            Name = config.AgentName,
            Description = config.Description ?? string.Empty,
            Version = config.Version ?? "1.0",
            Capabilities = new AgentCapabilities
            {
                Streaming = true,
                PushNotifications = true,
                ExtendedAgentCard = true
            },
            Skills = config.Skills.Select(s => new A2AAgentSkill
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description ?? string.Empty
            }).ToList(),
            SecuritySchemes = securitySchemes,
            SecurityRequirements = securityRequirements
        };
    }
}
