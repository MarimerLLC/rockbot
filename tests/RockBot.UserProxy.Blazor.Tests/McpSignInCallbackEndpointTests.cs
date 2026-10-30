using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Messaging;

namespace RockBot.UserProxy.Blazor.Tests;

/// <summary>
/// The MCP sign-in redirect URI (#738) over the real pipeline: it relays the authorization
/// server's redirect to the agent, and it is not a side door around the app's own sign-in.
/// </summary>
[TestClass]
public class McpSignInCallbackEndpointTests
{
    private sealed class CapturingPublisher : IMessagePublisher
    {
        public ConcurrentQueue<(string Topic, MessageEnvelope Envelope)> Published { get; } = new();

        public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Published.Enqueue((topic, envelope));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>Hosts the app without its hosted services, with the bus publisher captured.</summary>
    private sealed class Host(Dictionary<string, string?> settings, CapturingPublisher publisher) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);

            builder.ConfigureTestServices(services =>
            {
                foreach (var descriptor in services
                             .Where(d => d.ServiceType == typeof(IHostedService))
                             .Where(d => d.ImplementationType?.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) != true)
                             .ToList())
                    services.Remove(descriptor);

                services.AddSingleton<IMessagePublisher>(publisher);
            });
        }
    }

    private static HttpClient NoRedirects(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [TestMethod]
    public async Task Callback_RelaysTheRedirectToTheAgent()
    {
        var publisher = new CapturingPublisher();
        using var factory = new Host(new Dictionary<string, string?> { ["Agent:Name"] = "rockbot" }, publisher);
        using var client = NoRedirects(factory);

        var response = await client.GetAsync("/mcp-oauth/callback?code=abc&state=st&iss=https%3A%2F%2Fas.example");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "close this tab");

        var (topic, envelope) = publisher.Published.Single();
        Assert.AreEqual($"{UserProxyTopics.McpSignIn}.rockbot", topic);
        var callback = envelope.GetPayload<McpSignInCallback>()!;
        Assert.AreEqual("st", callback.State);
        Assert.AreEqual("abc", callback.Code);
        Assert.AreEqual("https://as.example", callback.Iss);
        Assert.IsNull(callback.Error);
    }

    [TestMethod]
    public async Task Callback_RelaysARefusal()
    {
        var publisher = new CapturingPublisher();
        using var factory = new Host([], publisher);
        using var client = NoRedirects(factory);

        var response = await client.GetAsync("/mcp-oauth/callback?error=access_denied&error_description=no&state=st");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "access_denied");
        var callback = publisher.Published.Single().Envelope.GetPayload<McpSignInCallback>()!;
        Assert.AreEqual("access_denied", callback.Error);
        Assert.AreEqual("no", callback.ErrorDescription);
        Assert.IsNull(callback.Code);
    }

    [TestMethod]
    public async Task Callback_WithoutState_IsRejected_AndNothingIsRelayed()
    {
        var publisher = new CapturingPublisher();
        using var factory = new Host([], publisher);
        using var client = NoRedirects(factory);

        var response = await client.GetAsync("/mcp-oauth/callback?code=abc");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsTrue(publisher.Published.IsEmpty);
    }

    [TestMethod]
    public async Task Callback_IsNotOpenToAnonymousCallers_WhenSignInIsOn()
    {
        var publisher = new CapturingPublisher();
        using var factory = new Host(new Dictionary<string, string?>
        {
            ["Auth:Enabled"] = "true",
            ["Auth:Providers:Google:ClientId"] = "client-id",
            ["Auth:Providers:Google:ClientSecret"] = "client-secret",
            ["Auth:AllowedDomains:0"] = "example.com",
        }, publisher);
        using var client = NoRedirects(factory);

        var response = await client.GetAsync("/mcp-oauth/callback?code=abc&state=st");

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.IsTrue(publisher.Published.IsEmpty, "an anonymous caller can't feed codes to the agent");
    }
}
