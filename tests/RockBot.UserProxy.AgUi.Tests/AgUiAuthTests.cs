using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// App mode (#794, phase 5): bearer tokens checked against a stand-in issuer, the allowlist, the
/// user id sent to the agent, access tickets, and a stream's end when its token runs out.
/// </summary>
[TestClass]
public sealed class AgUiAuthTests
{
    private const string Thread = "agui-session";

    /// <summary>An endpoint that needs no agent: it answers from the proxy's own state.</summary>
    private const string Probe = "/threads/agui-session/questions";

    private TestIssuer _issuer = null!;
    private AgUiTestHost _host = null!;
    private HttpClient _client = null!;
    private FakeAgent _agent = null!;
    private string _attachments = null!;
    private AuthTestClock? _clock;

    [TestInitialize]
    public void Init()
    {
        _issuer = new TestIssuer();
        _attachments = Path.Combine(Path.GetTempPath(), "rockbot-agui-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_attachments, "previews", "ab12"));
        Directory.CreateDirectory(Path.Combine(_attachments, "previews", "zz99"));
        File.WriteAllBytes(Path.Combine(_attachments, "chart.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_attachments, "other.png"), [4, 5, 6]);
        File.WriteAllText(Path.Combine(_attachments, "previews", "ab12", "deck.html"), "<img src=\"slide.png\">");
        File.WriteAllBytes(Path.Combine(_attachments, "previews", "ab12", "slide.png"), [7]);
        File.WriteAllBytes(Path.Combine(_attachments, "previews", "ab12", "photo.png"), [8]);
        File.WriteAllBytes(Path.Combine(_attachments, "previews", "zz99", "secret.png"), [9]);
    }

    /// <summary>Starts the proxy in app mode. The client sends no token unless a test adds one.</summary>
    private async Task StartAsync(bool withClock = false, bool overASocket = false, params (string Key, string Value)[] settings)
    {
        if (withClock)
            _clock = new AuthTestClock(DateTimeOffset.UtcNow);
        _host = new AgUiTestHost(TestIssuer.Settings(settings))
        {
            ConfigureServices = services =>
            {
                _issuer.Register(services);
                services.AddSingleton(new AttachmentPathResolver(_attachments));
                if (_clock is not null)
                    services.AddSingleton<TimeProvider>(_clock);
            },
        };
        if (overASocket)
        {
            // The real server parses a URL its own way (an encoded slash stays encoded, say).
            _host.UseKestrel(0);
            _host.StartServer();
        }
        _client = _host.CreateClient();
        _agent = await FakeAgent.StartAsync(_host);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_agent is not null)
            await _agent.DisposeAsync();
        _client?.Dispose();
        if (_host is not null)
            await _host.DisposeAsync();
        _issuer.Dispose();
        try { Directory.Delete(_attachments, recursive: true); } catch (IOException) { }
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static StringContent Run(string runId, string text = "Hello") => Json(
        $$"""{"threadId":"agui-session","runId":"{{runId}}","messages":[{"id":"m1","role":"user","content":"{{text}}"}]}""");

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // ── Token validation ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task GoodToken_IsLetIn()
    {
        await StartAsync();

        using var response = await GetAsync(Probe, _issuer.Token());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task NoToken_Is401_WithABearerChallenge_AndAProblemBody()
    {
        await StartAsync();

        using var response = await GetAsync(Probe, token: null);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("Bearer", response.Headers.WwwAuthenticate.ToString());
        var problem = await ProblemAsync(response);
        Assert.AreEqual(401, problem.GetProperty("status").GetInt32());
        StringAssert.Contains(problem.GetProperty("detail").GetString(), "Bearer");
    }

    [TestMethod]
    public async Task AnotherAuthorizationScheme_Is401()
    {
        await StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, Probe);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "c29tZW9uZTpwdw==");
        using var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task AssertInvalidTokenAsync(string token, string? says = null)
    {
        using var response = await GetAsync(Probe, token);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        StringAssert.StartsWith(challenge, "Bearer error=\"invalid_token\"");
        if (says is not null)
            StringAssert.Contains(challenge, says);
        Assert.AreEqual(401, (await ProblemAsync(response)).GetProperty("status").GetInt32());
    }

    [TestMethod]
    public async Task WrongAudience_Is401()
    {
        await StartAsync();

        // A real token from the right issuer, issued to some other app.
        await AssertInvalidTokenAsync(_issuer.Token(audience: "someone-elses-client-id"));
    }

    [TestMethod]
    public async Task EveryConfiguredAudience_IsAccepted()
    {
        await StartAsync();

        using var web = await GetAsync(Probe, _issuer.Token(audience: TestIssuer.WebClient));
        using var mobile = await GetAsync(Probe, _issuer.Token(audience: TestIssuer.MobileClient));

        Assert.AreEqual(HttpStatusCode.OK, web.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, mobile.StatusCode);
    }

    [TestMethod]
    public async Task WrongIssuer_Is401()
    {
        await StartAsync();

        await AssertInvalidTokenAsync(_issuer.Token(issuer: "https://another-issuer.test"));
    }

    [TestMethod]
    public async Task ExpiredToken_Is401_AndSaysSo()
    {
        await StartAsync();

        await AssertInvalidTokenAsync(_issuer.Token(lifetime: TimeSpan.FromMinutes(-5)), says: "expired");
    }

    [TestMethod]
    public async Task TokenSignedWithAnotherKey_Is401()
    {
        await StartAsync();

        await AssertInvalidTokenAsync(_issuer.Token(signingKey: _issuer.OtherKey));
    }

    [TestMethod]
    public async Task UnsignedToken_Is401()
    {
        await StartAsync();

        await AssertInvalidTokenAsync(_issuer.UnsignedToken());
    }

    [TestMethod]
    public async Task TokenSignedWithThePublicKeyAsASecret_Is401()
    {
        await StartAsync();

        // The classic algorithm confusion: HS256 with the issuer's public key as the secret.
        var secret = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            _issuer.Key.Rsa.ExportSubjectPublicKeyInfo()) { KeyId = _issuer.Key.KeyId };
        var forged = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(
            new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
            {
                Issuer = TestIssuer.Issuer, Audience = TestIssuer.WebClient,
                Claims = new Dictionary<string, object> { ["email"] = TestIssuer.AllowedEmail, ["email_verified"] = true },
                Expires = DateTime.UtcNow.AddMinutes(10),
                SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                    secret, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
            });

        await AssertInvalidTokenAsync(forged);
    }

    [TestMethod]
    public async Task GarbageToken_Is401()
    {
        await StartAsync();

        await AssertInvalidTokenAsync("not-a-token");
    }

    // ── The allowlist ────────────────────────────────────────────────────────

    private async Task AssertForbiddenAsync(string token, params string[] mustNotSay)
    {
        using var response = await GetAsync(Probe, token);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(403, (await ProblemAsync(response)).GetProperty("status").GetInt32());
        // Nothing about who is allowed, and not the address that was refused either.
        foreach (var secret in (string[])[TestIssuer.AllowedEmail, TestIssuer.AllowedDomain, .. mustNotSay])
            Assert.IsFalse(body.Contains(secret, StringComparison.OrdinalIgnoreCase), $"the body mentions '{secret}': {body}");
    }

    [TestMethod]
    public async Task AccountNotOnTheAllowlist_Is403_AndTheBodyNamesNoOne()
    {
        await StartAsync();

        await AssertForbiddenAsync(_issuer.Token(email: "stranger@elsewhere.test"), "stranger@elsewhere.test");
    }

    [TestMethod]
    public async Task DomainOnTheAllowlist_IsLetIn_AndALookalikeIsNot()
    {
        await StartAsync();

        using var allowed = await GetAsync(Probe, _issuer.Token(email: "Anyone@Corp.Example"));
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);

        await AssertForbiddenAsync(_issuer.Token(email: "attacker@evil-corp.example"));
        await AssertForbiddenAsync(_issuer.Token(email: "attacker@corp.example.evil.test"));
    }

    [TestMethod]
    public async Task UnverifiedEmail_Is403()
    {
        await StartAsync();

        await AssertForbiddenAsync(_issuer.Token(emailVerified: false));
    }

    [TestMethod]
    public async Task EmailWithNoVerificationClaim_Is403_UnlessTheProxyIsToldNotToRequireIt()
    {
        await StartAsync();
        await AssertForbiddenAsync(_issuer.Token(emailVerified: null));
        await Cleanup();

        Init();
        await StartAsync(settings: ("AgUi:Auth:RequireVerifiedEmail", "false"));
        using var lenient = await GetAsync(Probe, _issuer.Token(emailVerified: null));
        Assert.AreEqual(HttpStatusCode.OK, lenient.StatusCode);
        // An explicit false is refused either way: that is the shared allowlist's own rule.
        await AssertForbiddenAsync(_issuer.Token(emailVerified: false));
    }

    [TestMethod]
    public async Task TokenWithNoEmail_Is403()
    {
        await StartAsync();

        await AssertForbiddenAsync(_issuer.Token(email: null));
    }

    [TestMethod]
    public async Task GoogleTokens_MayNameTheIssuerWithoutAScheme()
    {
        // The default authority is Google, whose ID tokens carry either spelling of the issuer.
        using var google = new TestIssuer("https://accounts.google.com");
        _host = new AgUiTestHost(
            ("AgUi:Auth:Enabled", "true"),
            ("AgUi:Auth:Audiences:0", TestIssuer.WebClient),
            ("AgUi:Auth:AllowedEmails:0", TestIssuer.AllowedEmail))
        {
            ConfigureServices = google.Register,
        };
        _client = _host.CreateClient();
        _agent = await FakeAgent.StartAsync(_host);

        using var bare = await GetAsync(Probe, google.Token(issuer: "accounts.google.com"));
        using var full = await GetAsync(Probe, google.Token(issuer: "https://accounts.google.com"));
        using var other = await GetAsync(Probe, google.Token(issuer: "https://accounts.google.com.evil.test"));

        Assert.AreEqual(HttpStatusCode.OK, bare.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, full.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    // ── Which endpoints are locked ───────────────────────────────────────────

    /// <summary>Every endpoint a client calls: method, route pattern, and a URL that reaches it.</summary>
    internal static readonly (string Method, string Pattern, string Url)[] ClientEndpoints =
    [
        ("POST", "/agui", "/agui"),
        ("GET", "/agui/notify", "/agui/notify?threadId=agui-session"),
        ("POST", "/threads/{id}/cancel", "/threads/agui-session/cancel"),
        ("GET", "/threads/{id}/messages", "/threads/agui-session/messages"),
        ("POST", "/questions/{id}/answer", "/questions/q1/answer"),
        ("POST", "/attachments", "/attachments"),
        ("GET", "/attachments/{**path}", "/attachments/chart.png"),
        ("POST", "/tickets", "/tickets"),
        ("GET", "/t/{ticket}/attachments/{**path}", "/t/not-a-ticket/attachments/chart.png"),
        ("POST", "/feedback", "/feedback"),
        ("GET", "/saved", "/saved"),
        ("POST", "/saved", "/saved"),
        ("GET", "/saved/{id}", "/saved/1"),
        ("DELETE", "/saved/{id}", "/saved/1"),
        ("GET", "/mcp-signin", "/mcp-signin"),
        ("POST", "/mcp-signin/{server}/start", "/mcp-signin/scheduler/start"),
        ("POST", "/threads/{id}/clear", "/threads/agui-session/clear"),
        ("GET", "/threads/{id}/questions", "/threads/agui-session/questions"),
        ("GET", "/agent", "/agent"),
    ];

    internal static readonly string[] OpenEndpoints = ["/healthz", "/mcp-oauth/callback"];

    [TestMethod]
    public async Task EveryClientEndpoint_RefusesARequestWithoutAToken()
    {
        await StartAsync();

        foreach (var (method, _, url) in ClientEndpoints)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method != "GET" && method != "DELETE")
                request.Content = Json("{}");
            using var response = await _client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {url}");
        }

        Assert.AreEqual(0, _agent.Messages.Count, "nothing reached the agent");
    }

    [TestMethod]
    public async Task TheListOfClientEndpoints_IsEveryEndpointTheProxyMaps()
    {
        await StartAsync();

        // If this fails, an endpoint was added: put it in ClientEndpoints (so the test above
        // covers it) or, if it really must be open, in OpenEndpoints with a reason.
        var mapped = _host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(m => (Method: m, Pattern: e.RoutePattern.RawText!, Endpoint: e)))
            .ToList();

        CollectionAssert.AreEquivalent(
            ClientEndpoints.Select(e => $"{e.Method} {e.Pattern}").Concat(OpenEndpoints.Select(p => $"GET {p}")).ToList(),
            mapped.Select(e => $"{e.Method} {e.Pattern}").ToList());

        foreach (var (method, pattern, endpoint) in mapped)
        {
            var open = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var locked = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;
            Assert.AreEqual(OpenEndpoints.Contains(pattern), open, $"{method} {pattern}");
            Assert.AreNotEqual(open, locked, $"{method} {pattern}");
        }
    }

    [TestMethod]
    public async Task Healthz_StaysOpen()
    {
        await StartAsync();

        using var response = await _client.GetAsync("/healthz");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task TheSignInCallback_StaysOpen_ButOnlyRelaysASignInThatAnAuthenticatedCallerStarted()
    {
        await StartAsync();
        _agent.OnSignInStart = request => new McpSignInStartResponse
        {
            ServerName = request.ServerName, SignInId = "s1",
            AuthorizationUri = "https://login.example.com/authorize?client_id=c&state=st-123",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };

        // A browser arriving with a state nobody started: the page, not a 401, and nothing on the bus.
        using var unknown = await _client.GetAsync("/mcp-oauth/callback?code=abc&state=made-up");
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.AreEqual("text/html", unknown.Content.Headers.ContentType?.MediaType);

        // Starting a sign-in takes a token...
        using var anonymous = await _client.PostAsync("/mcp-signin/scheduler/start", null);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.AreEqual(0, _agent.Received<McpSignInStartRequest>().Count);

        var start = new HttpRequestMessage(HttpMethod.Post, "/mcp-signin/scheduler/start");
        start.Headers.Authorization = _issuer.Bearer();
        using var started = await _client.SendAsync(start);
        Assert.AreEqual(HttpStatusCode.OK, started.StatusCode);

        // ...and only then does the redirect, which carries none, get through. Once.
        using var redirect = await _client.GetAsync("/mcp-oauth/callback?code=abc&state=st-123");
        Assert.AreEqual(HttpStatusCode.OK, redirect.StatusCode);
        Assert.AreEqual("st-123", (await _agent.ReceivedAsync<McpSignInCallback>()).State);

        using var replay = await _client.GetAsync("/mcp-oauth/callback?code=abc&state=st-123");
        Assert.AreEqual(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.AreEqual(1, _agent.Received<McpSignInCallback>().Count);
    }

    // ── The user id ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task TheCallersEmail_IsTheUserIdTheAgentSees()
    {
        await StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/agui") { Content = Run("run-1") };
        request.Headers.Authorization = _issuer.Bearer(_issuer.Token(email: "Anyone@Corp.Example"));
        using var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "RUN_FINISHED");
        Assert.AreEqual("anyone@corp.example", _agent.Messages.Single().Message.UserId);
    }

    [TestMethod]
    public async Task TheCallersEmail_GoesWithAnAnswerAndAnUploadToo()
    {
        await StartAsync();
        _agent.OnUpload = _ => new AttachmentUploadResponse
        {
            Success = true, Attachment = new AgentAttachment { Mime = "image/png", Path = "up.png", FileName = "up.png" },
        };
        _client.DefaultRequestHeaders.Authorization = _issuer.Bearer();

        using var answer = await _client.PostAsync("/questions/q1/answer", Json("""{"answers":{"tz":"UTC"}}"""));
        Assert.AreEqual(HttpStatusCode.Accepted, answer.StatusCode);

        var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var upload = await _client.PostAsync("/attachments?fileName=up.png", file);
        Assert.AreEqual(HttpStatusCode.Created, upload.StatusCode);

        var deadline = DateTime.UtcNow + NotifyStream.Patience;
        while (_agent.Answers.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.AreEqual(TestIssuer.AllowedEmail, _agent.Answers.Single().UserId);
        Assert.AreEqual(TestIssuer.AllowedEmail, _agent.Uploads.Single().UserId);
    }

    [TestMethod]
    public async Task UserIdClaim_CanNameTheSubjectInstead()
    {
        await StartAsync(settings: ("AgUi:Auth:UserIdClaim", "sub"));

        var request = new HttpRequestMessage(HttpMethod.Post, "/agui") { Content = Run("run-1") };
        request.Headers.Authorization = _issuer.Bearer(_issuer.Token(subject: "user-42"));
        using var response = await _client.SendAsync(request);
        await response.Content.ReadAsStringAsync();

        Assert.AreEqual("user-42", _agent.Messages.Single().Message.UserId);
    }

    // ── Access tickets ───────────────────────────────────────────────────────

    private async Task<JsonElement> TicketAsync(object body, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/tickets") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = _issuer.Bearer(token);
        using var response = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private Task<JsonElement> AttachmentTicketAsync(string path) => TicketAsync(new { kind = "attachment", path });

    [TestMethod]
    public async Task AnAttachmentTicket_FetchesThatAttachment_WithNoAuthorizationHeader()
    {
        await StartAsync();

        var ticket = await AttachmentTicketAsync("chart.png");
        var url = ticket.GetProperty("url").GetString()!;

        StringAssert.StartsWith(url, "/t/");
        StringAssert.EndsWith(url, "/attachments/chart.png");
        Assert.IsTrue(ticket.GetProperty("expiresAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(1));

        using var response = await _client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        // The untrusted-content headers of the plain route, and the URL kept out of Referer.
        Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.AreEqual("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());

        // The plain URL still needs a token.
        using var plain = await _client.GetAsync("/attachments/chart.png");
        Assert.AreEqual(HttpStatusCode.Unauthorized, plain.StatusCode);
    }

    [TestMethod]
    public async Task AnAttachmentTicket_IsForThatFileOnly()
    {
        await StartAsync();
        var url = (await AttachmentTicketAsync("chart.png")).GetProperty("url").GetString()!;

        using var other = await _client.GetAsync(url.Replace("chart.png", "other.png"));
        using var nested = await _client.GetAsync(url.Replace("chart.png", "previews/ab12/slide.png"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, other.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, nested.StatusCode);
        StringAssert.Contains(other.Headers.WwwAuthenticate.ToString(), "invalid_token");
    }

    [TestMethod]
    public async Task AnHtmlAttachmentsTicket_CoversTheFilesBesideIt_AndNothingElse()
    {
        await StartAsync();
        var url = (await AttachmentTicketAsync("previews/ab12/deck.html")).GetProperty("url").GetString()!;
        var directory = url[..(url.LastIndexOf('/') + 1)];

        using var deck = await _client.GetAsync(url);
        // What the browser asks for when the deck says <img src="slide.png">.
        using var slide = await _client.GetAsync(directory + "slide.png");
        using var sibling = await _client.GetAsync(directory + "../zz99/secret.png");
        using var root = await _client.GetAsync(directory + "../../chart.png");

        Assert.AreEqual(HttpStatusCode.OK, deck.StatusCode);
        Assert.AreEqual("sandbox allow-scripts", deck.Headers.GetValues("Content-Security-Policy").Single());
        Assert.AreEqual(HttpStatusCode.OK, slide.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, sibling.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, root.StatusCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnHtmlAttachmentsTicket_CannotBeWalkedOutOfItsDirectory(bool overASocket)
    {
        await StartAsync(overASocket: overASocket);
        var url = (await AttachmentTicketAsync("previews/ab12/deck.html")).GetProperty("url").GetString()!;
        var directory = url[..(url.LastIndexOf('/') + 1)];

        // Each looks like a file under the deck's directory until something decodes it.
        string[] escapes =
        [
            "..%2F..%2Fchart.png",
            "..%2Fzz99%2Fsecret.png",
            "%2E%2E%2F%2E%2E%2Fchart.png",
            "..%5C..%5Cchart.png",
            "slide.png%2F..%2F..%2F..%2Fchart.png",
            "sub%2F..%2F..%2Fzz99%2Fsecret.png",
            "..%252F..%252Fchart.png",
        ];
        foreach (var escape in escapes)
        {
            using var response = await _client.GetAsync(directory + escape);
            Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode, escape);
        }

        using var slide = await _client.GetAsync(directory + "slide.png");
        Assert.AreEqual(HttpStatusCode.OK, slide.StatusCode);
    }

    [TestMethod]
    public async Task AnImagesTicket_DoesNotCoverTheFilesBesideIt()
    {
        await StartAsync();
        var url = (await AttachmentTicketAsync("previews/ab12/photo.png")).GetProperty("url").GetString()!;

        using var photo = await _client.GetAsync(url);
        using var slide = await _client.GetAsync(url.Replace("photo.png", "slide.png"));

        Assert.AreEqual(HttpStatusCode.OK, photo.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, slide.StatusCode);
    }

    [TestMethod]
    public async Task ATamperedTicket_Is401()
    {
        await StartAsync();
        var ticket = (await AttachmentTicketAsync("chart.png")).GetProperty("ticket").GetString()!;

        // The signature of one ticket on the payload of another, and a flipped signature.
        var forOther = (await AttachmentTicketAsync("other.png")).GetProperty("ticket").GetString()!;
        var spliced = forOther[..forOther.LastIndexOf('.')] + ticket[ticket.LastIndexOf('.')..];
        var flipped = ticket[..^1] + (ticket[^1] == 'A' ? 'B' : 'A');

        foreach (var bad in (string[])[spliced, flipped, "v1.e30.AAAA", "nonsense"])
        {
            using var response = await _client.GetAsync($"/t/{bad}/attachments/other.png");
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, bad);
        }
    }

    [TestMethod]
    public async Task AnExpiredTicket_Is401()
    {
        await StartAsync(withClock: true);
        var url = (await AttachmentTicketAsync("chart.png")).GetProperty("url").GetString()!;

        using var fresh = await _client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, fresh.StatusCode);

        _clock!.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));
        using var stale = await _client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [TestMethod]
    public async Task ATicket_NeverOutlivesTheTokenItWasIssuedFor()
    {
        await StartAsync();

        var ticket = await TicketAsync(new { kind = "attachment", path = "chart.png" },
            _issuer.Token(lifetime: TimeSpan.FromSeconds(30)));

        Assert.IsTrue(ticket.GetProperty("expiresAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow.AddSeconds(31));
    }

    [TestMethod]
    public async Task ATicket_IsNotACredentialAnywhereElse()
    {
        await StartAsync();
        var attachment = (await AttachmentTicketAsync("chart.png")).GetProperty("ticket").GetString()!;
        var notify = (await TicketAsync(new { kind = "notify", threadId = Thread })).GetProperty("ticket").GetString()!;

        foreach (var ticket in (string[])[attachment, notify])
        {
            using var query = await _client.GetAsync($"{Probe}?ticket={ticket}");
            // With the thread the notify ticket names, on an endpoint that also takes one.
            using var agentInfo = await _client.GetAsync($"/agent?threadId={Thread}&ticket={ticket}");
            Assert.AreEqual(HttpStatusCode.Unauthorized, agentInfo.StatusCode);
            using var plainAttachment = await _client.GetAsync($"/attachments/chart.png?ticket={ticket}");
            using var asBearer = await GetAsync(Probe, ticket);
            using var forMore = await _client.PostAsync($"/tickets?ticket={ticket}", Json("""{"kind":"notify"}"""));

            Assert.AreEqual(HttpStatusCode.Unauthorized, query.StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, plainAttachment.StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, asBearer.StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, forMore.StatusCode);
        }

        // Each kind only opens its own door.
        using var notifyAsAttachment = await _client.GetAsync($"/t/{notify}/attachments/chart.png");
        using var attachmentAsNotify = await _client.GetAsync($"/agui/notify?threadId={Thread}&ticket={attachment}");
        Assert.AreEqual(HttpStatusCode.Unauthorized, notifyAsAttachment.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, attachmentAsNotify.StatusCode);
    }

    [TestMethod]
    public async Task ANotifyTicket_OpensThatThreadsStream_AndNoOthers()
    {
        await StartAsync(settings: [("AgUi:AllowedThreadIds:0", Thread), ("AgUi:AllowedThreadIds:1", "phone")]);
        var ticket = await TicketAsync(new { kind = "notify", threadId = Thread });
        var url = ticket.GetProperty("url").GetString()!;
        StringAssert.StartsWith(url, $"/agui/notify?threadId={Thread}&ticket=");

        // As an EventSource would: no headers of its own.
        using (var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            StringAssert.StartsWith(await reader.ReadLineAsync().WaitAsync(NotifyStream.Patience), "id: ");
            StringAssert.Contains(await reader.ReadLineAsync().WaitAsync(NotifyStream.Patience), "rockbot.connected");
        }

        using var otherThread = await _client.GetAsync(url.Replace($"threadId={Thread}", "threadId=phone"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, otherThread.StatusCode);
    }

    [TestMethod]
    public async Task Tickets_AreRefusedForWhatTheCallerMayNotHave()
    {
        await StartAsync();
        _client.DefaultRequestHeaders.Authorization = _issuer.Bearer();

        using var badPath = await _client.PostAsync("/tickets", Json("""{"kind":"attachment","path":"../outside.png"}"""));
        using var noKind = await _client.PostAsync("/tickets", Json("""{"path":"chart.png"}"""));
        using var badThread = await _client.PostAsync("/tickets", Json("""{"kind":"notify","threadId":"not-allowed"}"""));

        Assert.AreEqual(HttpStatusCode.BadRequest, badPath.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, noKind.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, badThread.StatusCode);

        _client.DefaultRequestHeaders.Authorization = _issuer.Bearer(_issuer.Token(email: "stranger@elsewhere.test"));
        using var stranger = await _client.PostAsync("/tickets", Json("""{"kind":"attachment","path":"chart.png"}"""));
        Assert.AreEqual(HttpStatusCode.Forbidden, stranger.StatusCode);
    }

    // ── Streams and token expiry ─────────────────────────────────────────────

    [TestMethod]
    public async Task TheNotifyStream_EndsWhenTheTokenExpires()
    {
        await StartAsync();
        _client.DefaultRequestHeaders.Authorization = _issuer.Bearer(_issuer.Token(lifetime: TimeSpan.FromSeconds(3)));

        using var stream = await NotifyStream.OpenAsync(_client);
        await stream.ReadConnectedAsync();
        var opened = DateTime.UtcNow;

        // Nothing is sent; the proxy ends the stream by itself, well within the test's patience.
        Assert.IsNull(await stream.NextAsync(), "the stream should have ended");
        Assert.IsTrue(DateTime.UtcNow - opened < TimeSpan.FromSeconds(8));

        // Reconnecting with the stale token is refused; a fresh one carries on.
        using var stale = await _client.GetAsync($"/agui/notify?threadId={Thread}");
        Assert.AreEqual(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [TestMethod]
    public async Task ANotifyStreamOpenedWithATicket_EndsWhenTheTicketsTokenExpires()
    {
        await StartAsync();
        var ticket = await TicketAsync(new { kind = "notify", threadId = Thread }, _issuer.Token(lifetime: TimeSpan.FromSeconds(3)));
        var url = ticket.GetProperty("url").GetString()!;

        using var stream = await NotifyStream.OpenAsync(_client, query: url[url.IndexOf("&ticket=", StringComparison.Ordinal)..]);
        await stream.ReadConnectedAsync();

        Assert.IsNull(await stream.NextAsync(), "the stream should have ended");
    }

    [TestMethod]
    public async Task ARun_IsAllowedToFinishAfterItsTokenExpires()
    {
        await StartAsync();
        _agent.OnMessage = async (agent, received) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await agent.ReplyAsync(received, "Done, a little late");
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/agui") { Content = Run("run-1") };
        request.Headers.Authorization = _issuer.Bearer(_issuer.Token(lifetime: TimeSpan.FromSeconds(2)));
        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        StringAssert.Contains(body, "Done, a little late");
        StringAssert.Contains(body, "RUN_FINISHED");
    }
}

/// <summary>Network mode is what it was: no token is asked for, and the ticket endpoint hands back plain URLs.</summary>
[TestClass]
public sealed class AgUiNetworkModeTests
{
    [TestMethod]
    public async Task WithoutAppAuth_NothingAsksForAToken_AndTheUserIdIsTheConfiguredOne()
    {
        await using var host = new AgUiTestHost(("AgUi:DefaultUserId", "house"));
        using var client = host.CreateClient();
        await using var agent = await FakeAgent.StartAsync(host);

        // A token nobody could validate is simply not looked at.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "anything");
        using var response = await client.PostAsync("/agui", new StringContent(
            """{"threadId":"agui-session","runId":"r1","messages":[{"id":"m1","role":"user","content":"Hi"}]}""",
            Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();
        Assert.AreEqual("house", agent.Messages.Single().Message.UserId);
        Assert.IsFalse(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [TestMethod]
    public async Task WithoutAppAuth_ATicketIsThePlainUrl_AndThereIsNoTicketRoute()
    {
        await using var host = new AgUiTestHost();
        using var client = host.CreateClient();

        using var attachment = await client.PostAsync("/tickets", JsonContent.Create(new { kind = "attachment", path = "previews/ab12/deck.html" }));
        using var notify = await client.PostAsync("/tickets", JsonContent.Create(new { kind = "notify" }));
        using var ticketRoute = await client.GetAsync("/t/anything/attachments/chart.png");

        var attachmentBody = JsonDocument.Parse(await attachment.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual("/attachments/previews/ab12/deck.html", attachmentBody.GetProperty("url").GetString());
        Assert.IsFalse(attachmentBody.TryGetProperty("ticket", out _));
        Assert.AreEqual("/agui/notify?threadId=agui-session",
            JsonDocument.Parse(await notify.Content.ReadAsStringAsync()).RootElement.GetProperty("url").GetString());
        Assert.AreEqual(HttpStatusCode.NotFound, ticketRoute.StatusCode);
    }
}

/// <summary>CORS: only the configured origins, and nothing at all when none are.</summary>
[TestClass]
public sealed class AgUiCorsTests
{
    private const string App = "https://app.example.com";

    private static HttpRequestMessage Preflight(string url, string origin, string method, string headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return request;
    }

    private static HttpRequestMessage From(string origin, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [TestMethod]
    public async Task AnAllowedOrigin_IsToldSo_AndMayReadLocation()
    {
        await using var host = new AgUiTestHost(("AgUi:Cors:AllowedOrigins:0", App), ("AgUi:Cors:AllowedOrigins:1", "http://localhost:5173"));
        using var client = host.CreateClient();

        using var response = await client.SendAsync(From(App, HttpMethod.Get, "/threads/agui-session/questions"));
        using var second = await client.SendAsync(From("http://localhost:5173", HttpMethod.Get, "/threads/agui-session/questions"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(App, Header(response, "Access-Control-Allow-Origin"));
        StringAssert.Contains(Header(response, "Access-Control-Expose-Headers"), "Location");
        StringAssert.Contains(Header(response, "Access-Control-Expose-Headers"), "WWW-Authenticate");
        Assert.IsNull(Header(response, "Access-Control-Allow-Credentials"), "the API has no cookies to send");
        Assert.AreEqual("http://localhost:5173", Header(second, "Access-Control-Allow-Origin"));
    }

    [TestMethod]
    public async Task AnotherOrigin_GetsNoCorsHeaders()
    {
        await using var host = new AgUiTestHost(("AgUi:Cors:AllowedOrigins:0", App));
        using var client = host.CreateClient();

        using var response = await client.SendAsync(From("https://evil.example", HttpMethod.Get, "/threads/agui-session/questions"));
        using var lookalike = await client.SendAsync(From("https://app.example.com.evil.example", HttpMethod.Get, "/threads/agui-session/questions"));
        using var preflight = await client.SendAsync(Preflight("/agui", "https://evil.example", "POST", "content-type"));

        Assert.IsNull(Header(response, "Access-Control-Allow-Origin"));
        Assert.IsNull(Header(lookalike, "Access-Control-Allow-Origin"));
        Assert.IsNull(Header(preflight, "Access-Control-Allow-Origin"));
    }

    [TestMethod]
    public async Task APreflight_IsAnswered_WithTheHeadersAClientSends()
    {
        await using var host = new AgUiTestHost(("AgUi:Cors:AllowedOrigins:0", App));
        using var client = host.CreateClient();

        using var post = await client.SendAsync(Preflight("/agui", App, "POST", "authorization,content-type"));
        using var notify = await client.SendAsync(Preflight("/agui/notify?threadId=agui-session", App, "GET", "authorization,last-event-id,cache-control"));
        using var delete = await client.SendAsync(Preflight("/saved/1", App, "DELETE", "authorization"));
        using var put = await client.SendAsync(Preflight("/saved/1", App, "PUT", "authorization"));

        Assert.AreEqual(HttpStatusCode.NoContent, post.StatusCode);
        Assert.AreEqual(App, Header(post, "Access-Control-Allow-Origin"));
        StringAssert.Contains(Header(post, "Access-Control-Allow-Methods"), "POST");
        StringAssert.Contains(Header(post, "Access-Control-Allow-Headers"), "authorization");
        StringAssert.Contains(Header(post, "Access-Control-Allow-Headers"), "content-type");
        Assert.IsNotNull(Header(post, "Access-Control-Max-Age"));

        Assert.AreEqual(App, Header(notify, "Access-Control-Allow-Origin"));
        StringAssert.Contains(Header(notify, "Access-Control-Allow-Headers"), "last-event-id");
        Assert.AreEqual(App, Header(delete, "Access-Control-Allow-Origin"));
        // The answer lists what is allowed, and the browser refuses what isn't on it.
        Assert.AreEqual("GET,POST,DELETE", Header(put, "Access-Control-Allow-Methods"), "PUT is not a method of this API");
    }

    [TestMethod]
    public async Task WithNoOriginsConfigured_ThereAreNoCorsHeaders_AndAPreflightIsNotAnswered()
    {
        await using var host = new AgUiTestHost();
        using var client = host.CreateClient();

        using var response = await client.SendAsync(From(App, HttpMethod.Get, "/threads/agui-session/questions"));
        using var preflight = await client.SendAsync(Preflight("/agui", App, "POST", "content-type"));

        // Refused outright, not answered without the header: see AgUiOriginCheckTests.
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.IsNull(Header(response, "Access-Control-Allow-Origin"));
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, preflight.StatusCode);
        Assert.IsNull(Header(preflight, "Access-Control-Allow-Origin"));
    }

    [TestMethod]
    public async Task TheEventStreams_CarryTheHeaderToo()
    {
        await using var host = new AgUiTestHost(("AgUi:Cors:AllowedOrigins:0", App));
        using var client = host.CreateClient();
        await using var agent = await FakeAgent.StartAsync(host);

        using var notify = await client.SendAsync(
            From(App, HttpMethod.Get, "/agui/notify?threadId=agui-session"), HttpCompletionOption.ResponseHeadersRead);
        var run = From(App, HttpMethod.Post, "/agui");
        run.Content = new StringContent(
            """{"threadId":"agui-session","runId":"r1","messages":[{"id":"m1","role":"user","content":"Hi"}]}""",
            Encoding.UTF8, "application/json");
        using var ran = await client.SendAsync(run);

        Assert.AreEqual("text/event-stream", notify.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(App, Header(notify, "Access-Control-Allow-Origin"));
        Assert.AreEqual("text/event-stream", ran.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(App, Header(ran, "Access-Control-Allow-Origin"));
    }

    [TestMethod]
    public async Task InAppMode_APreflightNeedsNoToken_AndARefusalCanBeReadByTheApp()
    {
        using var issuer = new TestIssuer();
        await using var host = new AgUiTestHost(TestIssuer.Settings(("AgUi:Cors:AllowedOrigins:0", App)))
        {
            ConfigureServices = issuer.Register,
        };
        using var client = host.CreateClient();

        // A browser sends the preflight without the Authorization header it is asking about.
        using var preflight = await client.SendAsync(Preflight("/agui", App, "POST", "authorization,content-type"));
        using var refused = await client.SendAsync(From(App, HttpMethod.Get, "/threads/agui-session/questions"));
        var allowed = From(App, HttpMethod.Get, "/threads/agui-session/questions");
        allowed.Headers.Authorization = issuer.Bearer();
        using var ok = await client.SendAsync(allowed);

        Assert.AreEqual(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.AreEqual(App, Header(preflight, "Access-Control-Allow-Origin"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.AreEqual(App, Header(refused, "Access-Control-Allow-Origin"), "or the app could not tell a 401 from a network error");
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        Assert.AreEqual(App, Header(ok, "Access-Control-Allow-Origin"));
    }
}

/// <summary>An enabled configuration that can't work stops the proxy at startup.</summary>
[TestClass]
public sealed class AgUiAuthOptionsTests
{
    private static AgUiAuthOptions Usable() => new()
    {
        Enabled = true,
        Audiences = { "client-id" },
        AllowedEmails = { "someone@example.com" },
    };

    private static string Problems(AgUiAuthOptions options) => string.Join("\n", options.Validate());

    [TestMethod]
    public void Disabled_IsValid_WhateverElseIsSet()
    {
        Assert.AreEqual("", Problems(new AgUiAuthOptions()));
        Assert.AreEqual("", Problems(new AgUiAuthOptions { Authority = "nonsense", TicketKey = "!!" }));
    }

    [TestMethod]
    public void Enabled_WithAnAllowlistAndAnAudience_IsValid_AndDefaultsToGoogle()
    {
        var options = Usable();

        Assert.AreEqual("", Problems(options));
        Assert.AreEqual("https://accounts.google.com", options.Authority);
        CollectionAssert.AreEquivalent(
            new[] { "https://accounts.google.com", "accounts.google.com" }, options.ResolveExtraIssuers().ToList());
        Assert.IsTrue(options.RequireVerifiedEmail);
    }

    [TestMethod]
    public void Enabled_WithAnEmptyAllowlist_IsRejected()
    {
        var options = Usable();
        options.AllowedEmails.Clear();
        options.AllowedDomains.Add("  ");

        StringAssert.Contains(Problems(options), "AllowedEmails");
    }

    [TestMethod]
    public void Enabled_WithNoAudience_IsRejected()
    {
        var options = Usable();
        options.Audiences.Clear();
        options.Audiences.Add(" ");

        StringAssert.Contains(Problems(options), "Audiences");
    }

    [TestMethod]
    public void Enabled_WithAnAuthorityThatIsNotHttps_IsRejected()
    {
        foreach (var authority in (string[])["http://issuer.test", "issuer.test", "", "https://issuer.test/?x=1"])
        {
            var options = Usable();
            options.Authority = authority;
            StringAssert.Contains(Problems(options), "Authority", authority);
        }
    }

    [TestMethod]
    public void AnotherIssuer_GetsNoGoogleSpellings()
    {
        var options = Usable();
        options.Authority = "https://login.example.com/tenant";
        options.ValidIssuers.Add("https://sts.example.com/tenant");

        CollectionAssert.AreEqual(new[] { "https://sts.example.com/tenant" }, options.ResolveExtraIssuers().ToList());
    }

    [TestMethod]
    public void ATicketKey_MustBeBase64OfAtLeast32Bytes()
    {
        var options = Usable();

        options.TicketKey = "not base64!";
        StringAssert.Contains(Problems(options), "TicketKey");

        options.TicketKey = Convert.ToBase64String(new byte[16]);
        StringAssert.Contains(Problems(options), "TicketKey");

        options.TicketKey = Convert.ToBase64String(new byte[32]);
        Assert.AreEqual("", Problems(options));
    }

    [TestMethod]
    public void ATicketLifetime_IsShort()
    {
        var options = Usable();

        options.TicketLifetime = TimeSpan.FromHours(2);
        StringAssert.Contains(Problems(options), "TicketLifetime");
        options.TicketLifetime = TimeSpan.Zero;
        StringAssert.Contains(Problems(options), "TicketLifetime");
    }

    [TestMethod]
    public void TheProxy_DoesNotStart_EnabledWithNothingElse()
    {
        using var host = new AgUiTestHost(("AgUi:Auth:Enabled", "true"));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => host.CreateClient());

        StringAssert.Contains(error.Message, "AllowedEmails");
        StringAssert.Contains(error.Message, "Audiences");
    }

    [TestMethod]
    public void TheProxy_DoesNotStart_WithAnAllowlistButNoAudience()
    {
        using var host = new AgUiTestHost(("AgUi:Auth:Enabled", "true"), ("AgUi:Auth:AllowedDomains:0", "example.com"));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => host.CreateClient());

        StringAssert.Contains(error.Message, "Audiences");
        Assert.IsFalse(error.Message.Contains("AllowedEmails"), error.Message);
    }

    [TestMethod]
    public void CorsOrigins_AreOrigins()
    {
        var cors = new AgUiCorsOptions { AllowedOrigins = { " https://App.Example.com/ ", "http://localhost:5173", "", "https://app.example.com" } };

        CollectionAssert.AreEqual(new[] { "https://app.example.com", "http://localhost:5173" }, cors.ResolveOrigins().ToList());

        foreach (var bad in (string[])["*", "https://*.example.com", "app.example.com", "https://app.example.com/path", "null", "ftp://app.example.com"])
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => new AgUiCorsOptions { AllowedOrigins = { bad } }.ResolveOrigins(), bad);
        }
    }

    [TestMethod]
    public void TheProxy_DoesNotStart_WithAWildcardOrigin()
    {
        using var host = new AgUiTestHost(("AgUi:Cors:AllowedOrigins:0", "*"));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => host.CreateClient());

        StringAssert.Contains(error.Message, "AllowedOrigins");
    }
}

/// <summary>The ticket format by itself: signed, scoped, short-lived.</summary>
[TestClass]
public sealed class AccessTicketServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);

    [TestMethod]
    public void ATicket_SaysWhatItWasIssuedFor()
    {
        var clock = new AuthTestClock(Now);
        var tickets = new AccessTicketService(clock: clock);

        var (ticket, expiresAt) = tickets.Issue(AccessTicketKind.Attachment, "previews/ab/deck.html", "a@example.com", Now.AddHours(1), Life);
        var grant = tickets.Validate(ticket);

        Assert.AreEqual(Now + Life, expiresAt);
        Assert.IsNotNull(grant);
        Assert.AreEqual(AccessTicketKind.Attachment, grant.Kind);
        Assert.AreEqual("previews/ab/deck.html", grant.Scope);
        Assert.AreEqual("a@example.com", grant.Email);
        Assert.AreEqual(Now.AddHours(1), grant.TokenExpiresAt);
        // Safe in a path segment and in a query.
        Assert.IsTrue(ticket.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'), ticket);
    }

    [TestMethod]
    public void ATicket_ExpiresOnTime_AndNeverAfterItsToken()
    {
        var clock = new AuthTestClock(Now);
        var tickets = new AccessTicketService(clock: clock);

        var (ticket, _) = tickets.Issue(AccessTicketKind.Notify, "t", "a@example.com", Now.AddHours(1), Life);
        var (shortLived, shortExpiry) = tickets.Issue(AccessTicketKind.Notify, "t", "a@example.com", Now.AddSeconds(20), Life);
        Assert.AreEqual(Now.AddSeconds(20), shortExpiry);

        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.IsNotNull(tickets.Validate(ticket));
        Assert.IsNull(tickets.Validate(shortLived));

        clock.Advance(Life);
        Assert.IsNull(tickets.Validate(ticket));
    }

    [TestMethod]
    public void ATicket_FromAnotherKey_OrChanged_IsRefused()
    {
        var clock = new AuthTestClock(Now);
        var tickets = new AccessTicketService(clock: clock);
        var others = new AccessTicketService(clock: clock);
        var (ticket, _) = tickets.Issue(AccessTicketKind.Attachment, "a.png", "a@example.com", Now.AddHours(1), Life);

        Assert.IsNull(others.Validate(ticket), "another process's ticket");
        Assert.IsNull(tickets.Validate(ticket + "x"));
        Assert.IsNull(tickets.Validate(ticket.Replace("v1.", "v2.")));
        Assert.IsNull(tickets.Validate("v1." + ticket[3..].Replace('.', '!')));
        Assert.IsNull(tickets.Validate(""));
        Assert.IsNull(tickets.Validate(null));
        Assert.IsNull(tickets.Validate("v1.."));
        Assert.IsNull(tickets.Validate(new string('a', 5000)));
    }

    [TestMethod]
    public void ASharedKey_LetsReplicasAcceptEachOthersTickets()
    {
        var key = new byte[32];
        key[0] = 7;
        var clock = new AuthTestClock(Now);
        var (ticket, _) = new AccessTicketService(key, clock).Issue(AccessTicketKind.Notify, "t", "a@example.com", Now.AddHours(1), Life);

        Assert.IsNotNull(new AccessTicketService(key, clock).Validate(ticket));
    }

    [TestMethod]
    public void Coverage_IsExact_ExceptBesideAnHtmlDocument()
    {
        static AccessGrant For(AccessTicketKind kind, string scope) => new(kind, scope, "a@example.com", Now, Now);

        var image = For(AccessTicketKind.Attachment, "previews/ab/photo.png");
        Assert.IsTrue(image.CoversAttachment("previews/ab/photo.png"));
        Assert.IsFalse(image.CoversAttachment("previews/ab/other.png"));
        Assert.IsFalse(image.CoversNotify("previews/ab/photo.png"));

        var deck = For(AccessTicketKind.Attachment, "previews/ab/deck.html");
        Assert.IsTrue(deck.CoversAttachment("previews/ab/slide.png"));
        Assert.IsTrue(deck.CoversAttachment("previews/ab/img/slide.png"));
        Assert.IsFalse(deck.CoversAttachment("previews/abc/slide.png"));
        Assert.IsFalse(deck.CoversAttachment("previews/slide.png"));
        Assert.IsFalse(deck.CoversAttachment("previews/ab/../zz/slide.png"));
        Assert.IsFalse(deck.CoversAttachment("chart.png"));

        var rootPage = For(AccessTicketKind.Attachment, "report.html");
        Assert.IsTrue(rootPage.CoversAttachment("report.html"));
        Assert.IsFalse(rootPage.CoversAttachment("chart.png"), "an HTML file at the root does not unlock the whole directory");

        var notify = For(AccessTicketKind.Notify, "agui-session");
        Assert.IsTrue(notify.CoversNotify("agui-session"));
        Assert.IsFalse(notify.CoversNotify("phone"));
        Assert.IsFalse(notify.CoversNotify(null));
        Assert.IsFalse(notify.CoversAttachment("agui-session"));
    }
}
