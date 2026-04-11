using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.A2A.Gateway.Auth;

namespace RockBot.A2A.Gateway.Tests.Auth;

[TestClass]
public class ApiKeyAuthenticationHandlerTests
{
    private static readonly Dictionary<string, ApiKeyEntry> TestKeys = new()
    {
        ["valid-key-001"] = new ApiKeyEntry { AgentId = "partner-agent", DisplayName = "Partner Agent" },
        ["valid-key-002"] = new ApiKeyEntry { AgentId = "monitor-bot", DisplayName = "Monitor Bot" }
    };

    private static ApiKeyAuthenticationHandler CreateHandler(
        DefaultHttpContext httpContext,
        Dictionary<string, ApiKeyEntry>? keys = null)
    {
        var optionsMonitor = new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        var apiKeysMonitor = new TestOptionsMonitor<Dictionary<string, ApiKeyEntry>>(keys ?? TestKeys);
        var loggerFactory = NullLoggerFactory.Instance;

        var handler = new ApiKeyAuthenticationHandler(optionsMonitor, apiKeysMonitor, loggerFactory, System.Text.Encodings.Web.UrlEncoder.Default);
        var scheme = new AuthenticationScheme(ApiKeyAuthenticationHandler.SchemeName, null, typeof(ApiKeyAuthenticationHandler));
        handler.InitializeAsync(scheme, httpContext).GetAwaiter().GetResult();
        return handler;
    }

    [TestMethod]
    public async Task ValidKey_ReturnsSuccessWithCorrectClaims()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "valid-key-001";
        var handler = CreateHandler(context);

        var result = await handler.AuthenticateAsync();

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Principal);

        var agentId = result.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var displayName = result.Principal.FindFirst(ClaimTypes.Name)?.Value;
        var issuer = result.Principal.FindFirst("issuer")?.Value;

        Assert.AreEqual("partner-agent", agentId);
        Assert.AreEqual("Partner Agent", displayName);
        Assert.AreEqual("api-key", issuer);
    }

    [TestMethod]
    public async Task ValidKey_SecondKey_ReturnsCorrectIdentity()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "valid-key-002";
        var handler = CreateHandler(context);

        var result = await handler.AuthenticateAsync();

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("monitor-bot", result.Principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    }

    [TestMethod]
    public async Task MissingHeader_ReturnsNoResult()
    {
        var context = new DefaultHttpContext();
        // No X-Api-Key header
        var handler = CreateHandler(context);

        var result = await handler.AuthenticateAsync();

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.None);
    }

    [TestMethod]
    public async Task EmptyHeader_ReturnsFail()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "";
        var handler = CreateHandler(context);

        var result = await handler.AuthenticateAsync();

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.None);
        Assert.IsNotNull(result.Failure);
    }

    [TestMethod]
    public async Task InvalidKey_ReturnsFail()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "bad-key-999";
        var handler = CreateHandler(context);

        var result = await handler.AuthenticateAsync();

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.None);
        Assert.IsNotNull(result.Failure);
        Assert.IsTrue(result.Failure!.Message.Contains("Invalid"));
    }

    [TestMethod]
    public async Task NoConfiguredKeys_InvalidKey_ReturnsFail()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "any-key";
        var handler = CreateHandler(context, keys: []);

        var result = await handler.AuthenticateAsync();

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task HandleChallenge_ReturnsJsonRpcError()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handler = CreateHandler(context);

        await handler.InitializeAsync(
            new AuthenticationScheme(ApiKeyAuthenticationHandler.SchemeName, null, typeof(ApiKeyAuthenticationHandler)),
            context);
        await handler.ChallengeAsync(null);

        Assert.AreEqual(401, context.Response.StatusCode);
        Assert.AreEqual("application/json", context.Response.ContentType);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.IsTrue(body.Contains("\"jsonrpc\""));
        Assert.IsTrue(body.Contains("Authentication required"));
    }

    /// <summary>
    /// Minimal IOptionsMonitor implementation for testing.
    /// </summary>
    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
