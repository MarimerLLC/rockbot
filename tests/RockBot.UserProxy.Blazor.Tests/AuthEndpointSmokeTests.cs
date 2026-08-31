using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RockBot.UserProxy.Blazor.Tests;

/// <summary>
/// End-to-end checks over the real pipeline: middleware order, which endpoints are anonymous, and
/// that <c>/attachments</c> is not a side door around sign-in. These are the assertions that unit
/// tests structurally cannot make, because the thing under test is the composition.
/// </summary>
[TestClass]
public class AuthEndpointSmokeTests
{
    /// <summary>
    /// Hosts the app with the given configuration and none of its own hosted services. The
    /// message-bus listener is a hosted service that dials RabbitMQ on start; the HTTP surface does
    /// not need it, and requiring a live broker would make these an integration suite.
    /// </summary>
    private sealed class Host(Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // UseSetting, not ConfigureAppConfiguration: the app binds its Auth section during
            // service registration, before Build(), and only host-builder settings are in
            // WebApplicationBuilder.Configuration that early.
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);

            // ConfigureTestServices, not ConfigureServices: it runs after the app's own
            // registrations, which is the only point at which there is anything to remove.
            builder.ConfigureTestServices(RemoveApplicationHostedServices);
        }

        /// <summary>
        /// Drops the app's hosted services while leaving ASP.NET Core's own
        /// <c>GenericWebHostService</c> in place — removing every <see cref="IHostedService"/>
        /// would take the web server with it, and the TestServer then never starts.
        /// </summary>
        private static void RemoveApplicationHostedServices(IServiceCollection services)
        {
            var appHostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .Where(d => d.ImplementationType?.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) != true)
                .ToList();

            foreach (var descriptor in appHostedServices)
                services.Remove(descriptor);
        }
    }

    private static Host AuthEnabled() => new(new Dictionary<string, string?>
    {
        ["Auth:Enabled"] = "true",
        ["Auth:Providers:Google:ClientId"] = "client-id",
        ["Auth:Providers:Google:ClientSecret"] = "client-secret",
        ["Auth:AllowedDomains:0"] = "example.com",
    });

    private static Host AuthDisabled() => new([]);

    private static HttpClient NoRedirects(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Path of a redirect, whether the handler wrote a relative or an absolute Location.</summary>
    private static string? LocationPath(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null)
            return null;

        return location.IsAbsoluteUri
            ? location.AbsolutePath
            : location.OriginalString.Split('?')[0];
    }

    [TestMethod]
    public async Task AuthEnabled_HealthzIsAnonymous()
    {
        using var factory = AuthEnabled();
        using var client = NoRedirects(factory);

        var response = await client.GetAsync("/healthz");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AuthEnabled_LoginPageIsAnonymous()
    {
        using var factory = AuthEnabled();
        using var client = NoRedirects(factory);

        var response = await client.GetAsync("/login");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "/auth/challenge?provider=Google");
    }

    [TestMethod]
    public async Task AuthEnabled_ChatPageRedirectsAnonymousCallersToLogin()
    {
        using var factory = AuthEnabled();
        using var client = NoRedirects(factory);

        var response = await client.GetAsync("/");

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/login", LocationPath(response));
    }

    [TestMethod]
    public async Task AuthEnabled_AttachmentsIsNotServedToAnonymousCallers()
    {
        using var factory = AuthEnabled();
        using var client = NoRedirects(factory);

        // The side door: this endpoint serves PVC bytes, and before this change it had no
        // authorization at all. Whatever it answers, it must not be 200.
        var response = await client.GetAsync("/attachments?file=x");

        Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/login", LocationPath(response));
    }

    [TestMethod]
    public async Task AuthEnabled_ChallengeWithAnUnknownProviderGoesBackToLogin()
    {
        using var factory = AuthEnabled();
        using var client = NoRedirects(factory);

        // Not a 500 from challenging a scheme that was never registered.
        var response = await client.GetAsync("/auth/challenge?provider=Twitter");

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/login", response.Headers.Location?.OriginalString);
    }

    [TestMethod]
    public async Task AuthEnabled_ChallengeRedirectsToTheProvider()
    {
        using var factory = AuthEnabled();
        using var client = NoRedirects(factory);

        var response = await client.GetAsync("/auth/challenge?provider=Google&returnUrl=%2F");

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        StringAssert.StartsWith(response.Headers.Location?.OriginalString, "https://accounts.google.com/");
    }

    [TestMethod]
    public async Task AuthEnabled_ChallengeDoesNotForwardAnOffSiteReturnUrl()
    {
        using var factory = AuthEnabled();
        using var client = NoRedirects(factory);

        var response = await client.GetAsync("/auth/challenge?provider=Google&returnUrl=https%3A%2F%2Fevil.test%2F");

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? "";
        StringAssert.StartsWith(location, "https://accounts.google.com/");
        // The attacker's host must not survive into the state the provider echoes back.
        Assert.IsFalse(location.Contains("evil.test", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task AuthDisabled_EveryRouteBehavesAsItDidBefore()
    {
        using var factory = AuthDisabled();
        using var client = NoRedirects(factory);

        // /attachments carries .RequireAuthorization() unconditionally; with sign-in off the
        // default policy is permissive, so this must reach the handler and 404 on a missing file
        // rather than redirect to a login page that does not gate anything.
        var attachments = await client.GetAsync("/attachments?file=x");
        Assert.AreEqual(HttpStatusCode.NotFound, attachments.StatusCode);

        var health = await client.GetAsync("/healthz");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
    }

    [TestMethod]
    public async Task AuthDisabled_ChatPageRendersWithoutASignOutControl()
    {
        using var factory = AuthDisabled();
        using var client = NoRedirects(factory);

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        // With sign-in off the default policy succeeds for anonymous visitors, so AuthorizeView
        // alone would render a Sign out button posting to an endpoint that is not even mapped.
        Assert.IsFalse(html.Contains("/auth/logout", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AuthDisabled_AuthEndpointsAreNotMapped()
    {
        using var factory = AuthDisabled();
        using var client = NoRedirects(factory);

        var response = await client.GetAsync("/auth/challenge?provider=Google");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
