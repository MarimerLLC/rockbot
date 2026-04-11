using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace RockBot.A2A.Gateway.Auth;

/// <summary>
/// ASP.NET Core authentication handler that validates X-Api-Key headers
/// against configured API key entries and produces a ClaimsPrincipal
/// with the caller's agent identity.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    IOptionsMonitor<Dictionary<string, ApiKeyEntry>> apiKeys,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValues))
            return Task.FromResult(AuthenticateResult.NoResult());

        var apiKey = headerValues.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
            return Task.FromResult(AuthenticateResult.Fail("API key header is empty."));

        var keys = apiKeys.CurrentValue;
        if (!keys.TryGetValue(apiKey, out var entry))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, entry.AgentId),
            new Claim(ClaimTypes.Name, entry.DisplayName),
            new Claim("issuer", "api-key")
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.ContentType = "application/json";
        await Response.WriteAsync(
            """{"jsonrpc":"2.0","id":null,"error":{"code":-32000,"message":"Authentication required: provide X-Api-Key header"}}""");
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 403;
        Response.ContentType = "application/json";
        await Response.WriteAsync(
            """{"jsonrpc":"2.0","id":null,"error":{"code":-32000,"message":"Forbidden"}}""");
    }
}
