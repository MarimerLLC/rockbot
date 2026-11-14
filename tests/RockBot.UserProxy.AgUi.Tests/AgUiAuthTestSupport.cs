using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// A stand-in OIDC issuer for the app-mode tests: a signing key made here, and the discovery
/// document a real issuer would publish handed to the proxy directly, so no test reaches a network.
/// </summary>
internal sealed class TestIssuer : IDisposable
{
    public const string Issuer = "https://issuer.test";
    public const string WebClient = "web-client-id";
    public const string MobileClient = "mobile-client-id";
    public const string AllowedEmail = "someone@example.com";
    public const string AllowedDomain = "corp.example";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _otherRsa = RSA.Create(2048);
    private readonly string _issuer;

    public TestIssuer(string issuer = Issuer)
    {
        _issuer = issuer;
        Key = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
        // Same key id, different key: what a forged token would be signed with.
        OtherKey = new RsaSecurityKey(_otherRsa) { KeyId = "test-key" };
    }

    public RsaSecurityKey Key { get; }

    public RsaSecurityKey OtherKey { get; }

    /// <summary>The settings that turn app mode on against this issuer.</summary>
    public static (string, string)[] Settings(params (string Key, string Value)[] more) =>
    [
        ("AgUi:Auth:Enabled", "true"),
        ("AgUi:Auth:Authority", Issuer),
        ("AgUi:Auth:Audiences:0", WebClient),
        ("AgUi:Auth:Audiences:1", MobileClient),
        ("AgUi:Auth:AllowedEmails:0", AllowedEmail),
        ("AgUi:Auth:AllowedDomains:0", AllowedDomain),
        // Exact lifetimes, so a test can use a token that expires in a second or two.
        ("AgUi:Auth:ClockSkew", "00:00:00"),
        .. more,
    ];

    /// <summary>
    /// Gives the proxy this issuer's discovery document (its name and public key) in place of
    /// the one it would fetch from the authority.
    /// </summary>
    public void Register(IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = _issuer };
            configuration.SigningKeys.Add(new RsaSecurityKey(_rsa.ExportParameters(includePrivateParameters: false)) { KeyId = Key.KeyId });
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
        });

    /// <summary>An ID token as the issuer would sign it. Every argument has the value of a good token by default.</summary>
    public string Token(
        string? email = AllowedEmail,
        string? audience = WebClient,
        string? issuer = null,
        bool? emailVerified = true,
        TimeSpan? lifetime = null,
        SecurityKey? signingKey = null,
        string subject = "1234567890")
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (email is not null)
            claims["email"] = email;
        if (emailVerified is { } verified)
            claims["email_verified"] = verified;

        var now = DateTime.UtcNow;
        var life = lifetime ?? TimeSpan.FromMinutes(10);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? _issuer,
            Audience = audience,
            Claims = claims,
            // An expired token was issued, and ran out, in the past.
            NotBefore = life < TimeSpan.Zero ? now + life - TimeSpan.FromMinutes(10) : now - TimeSpan.FromSeconds(5),
            IssuedAt = life < TimeSpan.Zero ? now + life - TimeSpan.FromMinutes(10) : now - TimeSpan.FromSeconds(5),
            Expires = now + life,
            SigningCredentials = new SigningCredentials(signingKey ?? Key, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>A token with the right claims and no signature at all (<c>alg: none</c>).</summary>
    public string UnsignedToken()
    {
        static string Part(string json) => Base64UrlEncoder.Encode(json);
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        return Part("""{"alg":"none","typ":"JWT"}""") + "."
            + Part($$"""{"iss":"{{_issuer}}","aud":"{{WebClient}}","sub":"1","email":"{{AllowedEmail}}","email_verified":true,"exp":{{exp}}}""")
            + ".";
    }

    public AuthenticationHeaderValue Bearer(string? token = null) => new("Bearer", token ?? Token());

    public void Dispose()
    {
        _rsa.Dispose();
        _otherRsa.Dispose();
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class AuthTestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
