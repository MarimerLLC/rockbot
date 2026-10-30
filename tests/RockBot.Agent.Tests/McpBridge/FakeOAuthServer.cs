using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// A minimal OAuth 2.1 authorization server and MCP protected-resource guard, mounted on the
/// <see cref="BridgeHarness"/> fixture server: the MCP endpoint answers 401 with a
/// <c>resource_metadata</c> challenge until a request carries a token this server issued, as an
/// MCP server following the authorization spec does. Authorization-code + PKCE (S256) and refresh
/// only, for one pre-registered public client and any it registers dynamically.
/// </summary>
internal sealed class FakeOAuthServer
{
    public const string ClientId = "rockbot-test";

    private readonly ConcurrentDictionary<string, (string Challenge, string RedirectUri)> _codes = new();
    private readonly ConcurrentDictionary<string, byte> _accessTokens = new();
    private readonly ConcurrentDictionary<string, byte> _refreshTokens = new();
    private readonly ConcurrentDictionary<string, byte> _registeredClients = new();
    private int _registrations;
    private int _refreshGrants;
    private int _codeGrants;

    /// <summary>Times a refresh token was exchanged.</summary>
    public int RefreshGrants => Volatile.Read(ref _refreshGrants);

    /// <summary>Clients registered dynamically (RFC 7591).</summary>
    public int Registrations => Volatile.Read(ref _registrations);

    /// <summary>Times an authorization code was exchanged.</summary>
    public int CodeGrants => Volatile.Read(ref _codeGrants);

    /// <summary>Every <c>/authorize</c> request's redirect URI, newest last.</summary>
    public ConcurrentQueue<string> AuthorizeRedirects { get; } = new();

    /// <summary>Invalidates every access token, as their expiry would; refresh tokens stay good.</summary>
    public void ExpireAccessTokens() => _accessTokens.Clear();

    /// <summary>Invalidates every token, as revoking the user's grant would.</summary>
    public void RevokeAll()
    {
        _accessTokens.Clear();
        _refreshTokens.Clear();
    }

    /// <summary>Mounts the guard and the authorization server's endpoints on the fixture app.</summary>
    public void Mount(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp")
                && !IsAuthorized(context.Request.Headers.Authorization.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate =
                    $"Bearer resource_metadata=\"{BaseUrl(context)}/.well-known/oauth-protected-resource/mcp\"";
                return;
            }
            await next();
        });

        app.MapGet("/.well-known/oauth-protected-resource/mcp", (HttpContext context) => Results.Json(new Dictionary<string, object>
        {
            ["resource"] = $"{BaseUrl(context)}/mcp",
            ["authorization_servers"] = new[] { BaseUrl(context) },
            ["scopes_supported"] = new[] { "mcp.tools" },
        }));

        app.MapGet("/.well-known/oauth-authorization-server", (HttpContext context) => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = BaseUrl(context),
            ["authorization_endpoint"] = $"{BaseUrl(context)}/authorize",
            ["token_endpoint"] = $"{BaseUrl(context)}/token",
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = new[] { "none" },
            ["registration_endpoint"] = $"{BaseUrl(context)}/register",
        }));

        // Dynamic client registration: any caller gets a fresh public client.
        app.MapPost("/register", () =>
        {
            Interlocked.Increment(ref _registrations);
            var clientId = "dcr_" + NewToken();
            _registeredClients[clientId] = 0;
            return Results.Json(new Dictionary<string, object>
            {
                ["client_id"] = clientId,
                ["token_endpoint_auth_method"] = "none",
                ["redirect_uris"] = Array.Empty<string>(),
            }, statusCode: 201);
        });

        // The user's browser lands here; a real server would show a sign-in page first.
        app.MapGet("/authorize", (HttpContext context) =>
        {
            var q = context.Request.Query;
            var redirect = q["redirect_uri"].ToString();
            AuthorizeRedirects.Enqueue(redirect);
            var clientId = q["client_id"].ToString();
            if ((clientId != ClientId && !_registeredClients.ContainsKey(clientId))
                || q["response_type"] != "code" || q["code_challenge_method"] != "S256")
                return Results.BadRequest("bad authorization request");

            var code = NewToken();
            _codes[code] = (q["code_challenge"].ToString(), redirect);
            var separator = redirect.Contains('?') ? '&' : '?';
            return Results.Redirect(
                $"{redirect}{separator}code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(q["state"].ToString())}");
        });

        app.MapPost("/token", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            switch (form["grant_type"].ToString())
            {
                case "authorization_code":
                    if (!_codes.TryRemove(form["code"].ToString(), out var issued)
                        || issued.RedirectUri != form["redirect_uri"].ToString()
                        || issued.Challenge != S256(form["code_verifier"].ToString()))
                        return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
                    Interlocked.Increment(ref _codeGrants);
                    return Issue();

                case "refresh_token":
                    if (!_refreshTokens.TryRemove(form["refresh_token"].ToString(), out _))
                        return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
                    Interlocked.Increment(ref _refreshGrants);
                    return Issue();

                default:
                    return Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400);
            }
        });
    }

    private IResult Issue()
    {
        var access = NewToken();
        var refresh = NewToken();
        _accessTokens[access] = 0;
        _refreshTokens[refresh] = 0;
        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = access,
            ["token_type"] = "Bearer",
            ["expires_in"] = 3600,
            ["refresh_token"] = refresh,
            ["scope"] = "mcp.tools",
        });
    }

    private bool IsAuthorized(string header) =>
        header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        && _accessTokens.ContainsKey(header["Bearer ".Length..].Trim());

    private static string BaseUrl(HttpContext context) => $"{context.Request.Scheme}://{context.Request.Host}";

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string S256(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
