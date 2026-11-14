using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// The origin check (#794, phase 5): a browser request to a client endpoint must come from the
/// proxy's own origin or a configured one, in both modes. It is what stops a page on another
/// site from sending the proxy requests the browser doesn't preflight.
/// </summary>
[TestClass]
public sealed class AgUiOriginCheckTests
{
    private const string Evil = "https://evil.example";
    private const string App = "https://app.example.com";

    private AgUiTestHost _host = null!;
    private HttpClient _client = null!;
    private FakeAgent _agent = null!;

    private async Task StartAsync(params (string Key, string Value)[] settings)
    {
        _host = new AgUiTestHost(settings);
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
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? origin, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin);
        return request;
    }

    private const string RunBody =
        """{"threadId":"agui-session","runId":"r1","messages":[{"id":"m1","role":"user","content":"Delete everything"}]}""";

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string what)
    {
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, what);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType, what);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        StringAssert.Contains(problem.GetProperty("detail").GetString(), "origin");
        Assert.IsFalse(response.Headers.Contains("Access-Control-Allow-Origin"), what);
    }

    /// <summary>Nothing a refused request asked for reached the agent.</summary>
    private async Task AssertNothingReachedTheAgentAsync()
    {
        // Long enough for a message that did get published to arrive on the in-process bus.
        await Task.Delay(200);
        Assert.AreEqual(0, _agent.Messages.Count, "a run reached the agent");
        Assert.IsFalse(_agent.CancelRequested.IsCompleted, "a cancel reached the agent");
        Assert.AreEqual(0, _agent.Uploads.Count, "an upload reached the agent");
        Assert.AreEqual(0, _agent.Answers.Count, "an answer reached the agent");
        Assert.AreEqual(0, _agent.Received<ClearContextRequest>().Count, "a clear reached the agent");
        Assert.AreEqual(0, _agent.Received<UserFeedback>().Count, "feedback reached the agent");
        Assert.AreEqual(0, _agent.Received<SaveResponseRequest>().Count, "a save reached the agent");
        Assert.AreEqual(0, _agent.Received<DeleteSavedResponseRequest>().Count, "a delete reached the agent");
        Assert.AreEqual(0, _agent.Received<McpSignInStartRequest>().Count, "a sign-in start reached the agent");
    }

    // ── The requests a browser sends cross-site without a preflight ──────────

    [TestMethod]
    public async Task ACrossSiteRun_SentAsPlainText_IsRefused()
    {
        await StartAsync();

        // What <form enctype="text/plain"> or fetch(url, {method: "POST", body, mode: "no-cors"}) sends.
        using var response = await _client.SendAsync(Request(HttpMethod.Post, "/agui", Evil,
            new StringContent(RunBody, Encoding.UTF8, "text/plain")));

        await AssertRefusedAsync(response, "POST /agui");
        await AssertNothingReachedTheAgentAsync();
    }

    [TestMethod]
    public async Task ACrossSiteMultipartUpload_IsRefused()
    {
        await StartAsync();
        _agent.OnUpload = _ => new AttachmentUploadResponse
        {
            Success = true, Attachment = new AgentAttachment { Mime = "image/png", Path = "x.png" },
        };
        var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var form = new MultipartFormDataContent { { file, "file", "x.png" } };

        using var response = await _client.SendAsync(Request(HttpMethod.Post, "/attachments", Evil, form));

        await AssertRefusedAsync(response, "POST /attachments");
        await AssertNothingReachedTheAgentAsync();
    }

    [TestMethod]
    public async Task CrossSitePostsWithNoBody_AreRefused()
    {
        await StartAsync();

        foreach (var url in (string[])["/threads/agui-session/cancel", "/threads/agui-session/clear", "/mcp-signin/scheduler/start"])
        {
            using var response = await _client.SendAsync(Request(HttpMethod.Post, url, Evil));
            await AssertRefusedAsync(response, url);
        }

        await AssertNothingReachedTheAgentAsync();
    }

    [TestMethod]
    public async Task CrossSitePostsWithAFormOrTextBody_AreRefused()
    {
        await StartAsync();
        (string Url, string Body)[] posts =
        [
            ("/feedback", """{"messageId":"m1","isPositive":false}"""),
            ("/saved", """{"label":"x","content":"y"}"""),
            ("/questions/q1/answer", """{"decline":true}"""),
            ("/tickets", """{"kind":"notify"}"""),
        ];

        foreach (var (url, body) in posts)
        {
            using var response = await _client.SendAsync(Request(HttpMethod.Post, url, Evil,
                new StringContent(body, Encoding.UTF8, "text/plain")));
            await AssertRefusedAsync(response, url);
        }

        await AssertNothingReachedTheAgentAsync();
    }

    [TestMethod]
    public async Task EveryClientEndpoint_RefusesAnotherOrigin_WhateverTheMethod()
    {
        await StartAsync();

        foreach (var (method, _, url) in AgUiAuthTests.ClientEndpoints.Where(e => !e.Pattern.StartsWith("/t/", StringComparison.Ordinal)))
        {
            var content = method is "GET" or "DELETE" ? null : new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(Request(new HttpMethod(method), url, Evil, content));
            await AssertRefusedAsync(response, $"{method} {url}");
        }

        await AssertNothingReachedTheAgentAsync();
    }

    [TestMethod]
    public async Task EveryClientEndpoint_IsOriginChecked_AndTheOpenOnesAreNot()
    {
        await StartAsync();

        // An endpoint added without the group would be missing here, and the test above would not see it.
        foreach (var endpoint in _host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText!;
            Assert.AreEqual(
                !AgUiAuthTests.OpenEndpoints.Contains(pattern),
                endpoint.Metadata.GetMetadata<OriginCheckedMetadata>() is not null,
                pattern);
        }
    }

    [TestMethod]
    public async Task TheNullOrigin_OfASandboxedDocument_IsRefused()
    {
        await StartAsync(("AgUi:Cors:AllowedOrigins:0", App));

        // An HTML attachment is served sandboxed, so its scripts run in an opaque origin.
        using var run = await _client.SendAsync(Request(HttpMethod.Post, "/agui", "null",
            new StringContent(RunBody, Encoding.UTF8, "text/plain")));
        using var cancel = await _client.SendAsync(Request(HttpMethod.Post, "/threads/agui-session/cancel", "null"));

        await AssertRefusedAsync(run, "run");
        await AssertRefusedAsync(cancel, "cancel");
        await AssertNothingReachedTheAgentAsync();
    }

    // ── What still gets through ──────────────────────────────────────────────

    private async Task AssertRunsAsync(string? origin, string runId = "r1")
    {
        using var response = await _client.SendAsync(Request(HttpMethod.Post, "/agui", origin,
            new StringContent(RunBody.Replace("\"r1\"", $"\"{runId}\""), Encoding.UTF8, "application/json")));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, origin);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "RUN_FINISHED");
    }

    [TestMethod]
    public async Task ARequestWithNoOrigin_Passes()
    {
        await StartAsync();

        // curl, a phone app, a server.
        await AssertRunsAsync(origin: null);
        using var cancel = await _client.SendAsync(Request(HttpMethod.Post, "/threads/agui-session/cancel", origin: null));
        Assert.AreEqual(HttpStatusCode.Accepted, cancel.StatusCode);
    }

    [TestMethod]
    public async Task TheProxysOwnOrigin_Passes_WhateverTheSchemeTheBrowserSaw()
    {
        await StartAsync();

        // The test server's address is http://localhost. Behind something that terminates TLS
        // the browser's origin says https and the proxy sees http.
        await AssertRunsAsync("http://localhost", "r1");
        await AssertRunsAsync("https://localhost", "r2");
        await AssertRunsAsync("HTTP://LOCALHOST", "r3");

        // Another port, or a host that merely contains it, is another origin.
        foreach (var other in (string[])["http://localhost:8081", "http://localhost.evil.example", "http://evil.example/localhost", "localhost", "http://"])
        {
            using var response = await _client.SendAsync(Request(HttpMethod.Post, "/threads/agui-session/cancel", other));
            await AssertRefusedAsync(response, other);
        }
    }

    [TestMethod]
    public async Task ThePublicBaseUrlsOrigin_Passes_WhenTheHostHeaderIsSomethingElse()
    {
        // An ingress that rewrites Host: the proxy can't recognise its own origin from the
        // request, so it is told.
        await StartAsync(("AgUi:PublicBaseUrl", "https://agui.example.com/"));

        await AssertRunsAsync("https://agui.example.com");
        using var plainHttp = await _client.SendAsync(Request(HttpMethod.Post, "/threads/agui-session/cancel", "http://agui.example.com"));
        await AssertRefusedAsync(plainHttp, "the same host over http");
    }

    [TestMethod]
    public async Task AConfiguredOrigin_Passes_AndAnotherDoesNot()
    {
        await StartAsync(("AgUi:Cors:AllowedOrigins:0", App));

        await AssertRunsAsync(App);
        using var evil = await _client.SendAsync(Request(HttpMethod.Post, "/agui", Evil,
            new StringContent(RunBody, Encoding.UTF8, "text/plain")));
        await AssertRefusedAsync(evil, Evil);
        Assert.AreEqual(1, _agent.Messages.Count);
    }

    [TestMethod]
    public async Task TwoOriginHeaders_AreRefused()
    {
        await StartAsync(("AgUi:Cors:AllowedOrigins:0", App));
        var request = new HttpRequestMessage(HttpMethod.Post, "/threads/agui-session/cancel");
        request.Headers.TryAddWithoutValidation("Origin", [App, Evil]);

        using var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task HealthAndTheSignInRedirect_AreNotOriginChecked()
    {
        await StartAsync();

        using var health = await _client.SendAsync(Request(HttpMethod.Get, "/healthz", Evil));
        using var callback = await _client.SendAsync(Request(HttpMethod.Get, "/mcp-oauth/callback?code=a&state=unknown", "https://login.example.com"));

        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        // The "not under way" page, as without the header: the redirect is judged by its state.
        Assert.AreEqual(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.AreEqual("text/html", callback.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task InAppMode_AnotherOriginIsRefused_EvenWithAValidToken_AndTicketsStillWork()
    {
        using var issuer = new TestIssuer();
        var attachments = Path.Combine(Path.GetTempPath(), "rockbot-agui-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachments);
        File.WriteAllBytes(Path.Combine(attachments, "chart.png"), [1, 2, 3]);
        try
        {
            _host = new AgUiTestHost(TestIssuer.Settings(("AgUi:Cors:AllowedOrigins:0", App)))
            {
                ConfigureServices = services =>
                {
                    issuer.Register(services);
                    services.AddSingleton(new AttachmentPathResolver(attachments));
                },
            };
            _client = _host.CreateClient();
            _agent = await FakeAgent.StartAsync(_host);

            var evil = Request(HttpMethod.Post, "/threads/agui-session/cancel", Evil);
            evil.Headers.Authorization = issuer.Bearer();
            using var refused = await _client.SendAsync(evil);
            await AssertRefusedAsync(refused, "a token from another origin");

            // The app asks for tickets from its own origin...
            var ask = Request(HttpMethod.Post, "/tickets", App,
                new StringContent("""{"kind":"attachment","path":"chart.png"}""", Encoding.UTF8, "application/json"));
            ask.Headers.Authorization = issuer.Bearer();
            using var asked = await _client.SendAsync(ask);
            var url = JsonDocument.Parse(await asked.Content.ReadAsStringAsync()).RootElement.GetProperty("url").GetString()!;
            var askNotify = Request(HttpMethod.Post, "/tickets", App,
                new StringContent("""{"kind":"notify"}""", Encoding.UTF8, "application/json"));
            askNotify.Headers.Authorization = issuer.Bearer();
            using var askedNotify = await _client.SendAsync(askNotify);
            var notifyUrl = JsonDocument.Parse(await askedNotify.Content.ReadAsStringAsync()).RootElement.GetProperty("url").GetString()!;

            // ...an <img> or a new tab fetches the attachment with no Origin at all...
            using var image = await _client.SendAsync(Request(HttpMethod.Get, url, origin: null));
            Assert.AreEqual(HttpStatusCode.OK, image.StatusCode);

            // ...and an EventSource names the app's origin, which is allowed; another page's is not.
            using var stream = await _client.SendAsync(Request(HttpMethod.Get, notifyUrl, App), HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual(HttpStatusCode.OK, stream.StatusCode);
            Assert.AreEqual(App, stream.Headers.GetValues("Access-Control-Allow-Origin").Single());
            using var stolen = await _client.SendAsync(Request(HttpMethod.Get, notifyUrl, Evil));
            await AssertRefusedAsync(stolen, "a ticket used from another origin");
        }
        finally
        {
            try { Directory.Delete(attachments, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>The rule by itself.</summary>
[TestClass]
public sealed class OriginCheckTests
{
    private static HttpRequest Request(string host, params string[] origins)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        if (origins.Length > 0)
            context.Request.Headers.Origin = origins;
        return context.Request;
    }

    [TestMethod]
    public void TheRule()
    {
        var options = new AgUiOptions { PublicBaseUrl = "https://agui.example.com:8443/base" };
        options.Cors.AllowedOrigins.Add("https://App.example.com");
        var check = new OriginCheck(options);

        Assert.IsTrue(check.Allows(Request("internal:8080")), "no Origin");
        Assert.IsTrue(check.Allows(Request("internal:8080", "https://app.example.com")), "a configured origin");
        Assert.IsTrue(check.Allows(Request("internal:8080", "https://agui.example.com:8443")), "the public address");
        Assert.IsTrue(check.Allows(Request("internal:8080", "http://internal:8080")), "the address the request was sent to");
        Assert.IsTrue(check.Allows(Request("rockbot-agui", "https://rockbot-agui")), "the same host behind TLS termination");

        Assert.IsFalse(check.Allows(Request("internal:8080", "null")));
        Assert.IsFalse(check.Allows(Request("internal:8080", "")));
        Assert.IsFalse(check.Allows(Request("internal:8080", "https://evil.example")));
        Assert.IsFalse(check.Allows(Request("internal:8080", "http://internal")), "another port");
        Assert.IsFalse(check.Allows(Request("internal:8080", "https://agui.example.com")), "the public host on another port");
        Assert.IsFalse(check.Allows(Request("internal:8080", "https://app.example.com.evil.example")));
        Assert.IsFalse(check.Allows(Request("internal:8080", "https://app.example.com", "https://evil.example")));
    }
}
