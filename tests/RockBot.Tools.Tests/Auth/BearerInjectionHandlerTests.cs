using System.Net;
using System.Net.Http.Headers;
using RockBot.Tools.Mcp.Auth;

namespace RockBot.Tools.Tests.Auth;

[TestClass]
public class BearerInjectionHandlerTests
{
    [TestMethod]
    public async Task SendAsync_InjectsBearerOnEveryRequest()
    {
        var provider = new RecordingProvider(_ => "token-A");
        var inner = new StubInner(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new BearerInjectionHandler(provider, inner);
        var client = new HttpClient(handler);

        await client.GetAsync("https://srv/");
        await client.GetAsync("https://srv/");
        await client.GetAsync("https://srv/");

        Assert.AreEqual(3, inner.AuthHeaders.Count);
        foreach (var header in inner.AuthHeaders)
        {
            Assert.AreEqual("Bearer", header?.Scheme);
            Assert.AreEqual("token-A", header?.Parameter);
        }
        Assert.AreEqual(3, provider.Calls.Count);
        Assert.IsTrue(provider.Calls.All(forceRefresh => forceRefresh == false));
    }

    [TestMethod]
    public async Task SendAsync_On401_ForcesRefreshAndRetriesOnce()
    {
        var tokens = new Queue<string>(["stale-token", "fresh-token"]);
        var provider = new RecordingProvider(_ => tokens.Dequeue());

        var responses = new Queue<HttpResponseMessage>([
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.OK)
        ]);
        var inner = new StubInner(_ => responses.Dequeue());

        var handler = new BearerInjectionHandler(provider, inner);
        var client = new HttpClient(handler);

        var result = await client.GetAsync("https://srv/");

        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        Assert.AreEqual(2, inner.AuthHeaders.Count);
        Assert.AreEqual("stale-token", inner.AuthHeaders[0]?.Parameter);
        Assert.AreEqual("fresh-token", inner.AuthHeaders[1]?.Parameter);
        CollectionAssert.AreEqual(new[] { false, true }, provider.Calls);
    }

    [TestMethod]
    public async Task SendAsync_DoubleUnauthorized_ThrowsMcpAuthChallengeException()
    {
        var provider = new RecordingProvider(_ => "any-token");
        var responses = new Queue<HttpResponseMessage>([
            BuildUnauthorized("Bearer error=\"invalid_token\""),
            BuildUnauthorized("Bearer resource_metadata=\"https://srv/.well-known/oauth-protected-resource\"")
        ]);
        var inner = new StubInner(_ => responses.Dequeue());

        var handler = new BearerInjectionHandler(provider, inner);
        var client = new HttpClient(handler);

        var ex = await Assert.ThrowsExactlyAsync<McpAuthChallengeException>(
            () => client.GetAsync("https://srv/"));

        Assert.IsNotNull(ex.Challenge);
        Assert.AreEqual("https://srv/.well-known/oauth-protected-resource", ex.Challenge.ResourceMetadata);
        StringAssert.Contains(ex.Message, "https://srv/.well-known/oauth-protected-resource");
        Assert.AreEqual(2, inner.AuthHeaders.Count);
    }

    [TestMethod]
    public async Task SendAsync_DoubleUnauthorizedWithoutChallenge_ThrowsWithoutChallenge()
    {
        var provider = new RecordingProvider(_ => "any-token");
        var responses = new Queue<HttpResponseMessage>([
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
        ]);
        var inner = new StubInner(_ => responses.Dequeue());

        var handler = new BearerInjectionHandler(provider, inner);
        var client = new HttpClient(handler);

        var ex = await Assert.ThrowsExactlyAsync<McpAuthChallengeException>(
            () => client.GetAsync("https://srv/"));

        Assert.IsNull(ex.Challenge);
    }

    [TestMethod]
    public async Task SendAsync_OverwritesExistingAuthorizationHeader()
    {
        var provider = new RecordingProvider(_ => "injected-token");
        var inner = new StubInner(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new BearerInjectionHandler(provider, inner);
        var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://srv/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "caller-set-value");

        await client.SendAsync(request);

        Assert.AreEqual("Bearer", inner.AuthHeaders[0]?.Scheme);
        Assert.AreEqual("injected-token", inner.AuthHeaders[0]?.Parameter);
    }

    private static HttpResponseMessage BuildUnauthorized(string wwwAuthenticate)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var spaceIdx = wwwAuthenticate.IndexOf(' ');
        if (spaceIdx > 0)
        {
            resp.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                wwwAuthenticate[..spaceIdx], wwwAuthenticate[(spaceIdx + 1)..]));
        }
        else
        {
            resp.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(wwwAuthenticate));
        }
        return resp;
    }

    private sealed class RecordingProvider(Func<bool, string> tokenFactory) : ITokenProvider
    {
        public List<bool> Calls { get; } = new();

        public Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
        {
            Calls.Add(forceRefresh);
            return Task.FromResult(tokenFactory(forceRefresh));
        }
    }

    private sealed class StubInner(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        // We snapshot the Authorization header at send time because
        // BearerInjectionHandler reuses the same HttpRequestMessage on retry,
        // mutating headers in place. Capturing references would show the final
        // state for every call.
        public List<AuthenticationHeaderValue?> AuthHeaders { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthHeaders.Add(request.Headers.Authorization);
            return Task.FromResult(respond(request));
        }
    }
}
