using System.Web;
using ModelContextProtocol.Server;
using RockBot.Agent.McpBridge;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// End-to-end coverage of MCP OAuth sign-in (#738) against an OAuth-protected fixture server: the
/// bridge reports that a server needs the user to sign in, a user interface starts the sign-in and
/// relays the browser's redirect, and the bridge connects, keeps and refreshes the tokens. See
/// <see cref="FakeOAuthServer"/> and <c>design/mcp-oauth.md</c>.
/// </summary>
[TestClass]
public class McpOAuthEndToEndTests
{
    private const string AgentName = "test-agent";
    private const string RedirectUri = "https://rockbot.example/mcp-oauth/callback";

    private static string SignInTopic => $"{UserProxyTopics.McpSignIn}.{AgentName}";
    private static string BroadcastTopic => $"{UserProxyTopics.UserResponse}.{AgentName}";

    private static McpServerTool Echo() => McpServerTool.Create(
        (string text) => $"echo:{text}",
        new McpServerToolCreateOptions { Name = "echo" });

    private static async Task<(BridgeHarness Harness, FakeOAuthServer Auth)> StartAsync(
        string? clientId = FakeOAuthServer.ClientId)
    {
        var auth = new FakeOAuthServer();
        var harness = await BridgeHarness.StartAsync(
            [Echo()],
            configure: entry => entry.Auth = new McpServerAuthConfig
            {
                OAuth = new McpServerOAuthConfig { ClientId = clientId }
            },
            configureServerApp: auth.Mount);
        return (harness, auth);
    }

    private static IEnumerable<McpSignInStatus> Statuses(BridgeHarness harness) =>
        harness.PublishedOn(BroadcastTopic)
            .Where(e => e.MessageType == typeof(McpSignInStatus).FullName)
            .Select(e => e.GetPayload<McpSignInStatus>()!);

    private static async Task<McpSignInStatus> WaitForStatusAsync(BridgeHarness harness, string state, string? signInId = null)
    {
        var envelope = await harness.WaitForAsync(BroadcastTopic, e =>
            e.MessageType == typeof(McpSignInStatus).FullName
            && e.GetPayload<McpSignInStatus>() is { } status
            && status.State == state
            && (signInId is null || status.SignInId == signInId));
        return envelope.GetPayload<McpSignInStatus>()!;
    }

    private static async Task<McpSignInStartResponse> StartSignInAsync(BridgeHarness harness, string server = BridgeHarness.ServerName)
    {
        var reply = await harness.RequestAsync(SignInTopic,
            new McpSignInStartRequest { ServerName = server, RedirectUri = RedirectUri });
        return reply.GetPayload<McpSignInStartResponse>()!;
    }

    /// <summary>Does what the user's browser does: opens the authorization URL and returns where it was sent back to.</summary>
    private static async Task<Uri> FollowInBrowserAsync(string authorizationUri)
    {
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var response = await browser.GetAsync(authorizationUri);
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, response.StatusCode, await response.Content.ReadAsStringAsync());
        return response.Headers.Location!;
    }

    private static McpSignInCallback CallbackFrom(Uri redirect)
    {
        var query = HttpUtility.ParseQueryString(redirect.Query);
        return new McpSignInCallback { State = query["state"]!, Code = query["code"], Iss = query["iss"] };
    }

    private static async Task SignInAsync(BridgeHarness harness)
    {
        var start = await StartSignInAsync(harness);
        Assert.IsNull(start.Error, start.Error);
        var redirect = await FollowInBrowserAsync(start.AuthorizationUri!);
        await harness.DeliverAsync(SignInTopic, CallbackFrom(redirect));
        await WaitForStatusAsync(harness, McpSignInState.Completed, start.SignInId);
    }

    [TestMethod]
    public async Task Unauthenticated_ServerNeedsSignIn_AndCallsSayHowToSignIn()
    {
        var (harness, _) = await StartAsync();
        await using var _harness = harness;

        var required = await WaitForStatusAsync(harness, McpSignInState.Required);
        Assert.AreEqual(BridgeHarness.ServerName, required.ServerName);

        var response = await harness.InvokeAsync("echo", """{"text":"hi"}""");
        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "needs the user to sign in");
        StringAssert.Contains(response.Content, $"rockbot auth mcp {BridgeHarness.ServerName}");

        // Reported once, not on every attempt.
        Assert.AreEqual(1, Statuses(harness).Count(s => s.State == McpSignInState.Required));
    }

    [TestMethod]
    public async Task SignIn_ConnectsTheServer_AndItsToolsWork()
    {
        var (harness, auth) = await StartAsync();
        await using var _harness = harness;

        var start = await StartSignInAsync(harness);
        Assert.IsNull(start.Error, start.Error);
        Assert.IsNotNull(start.SignInId);
        StringAssert.StartsWith(start.AuthorizationUri, $"{harness.ServerUrl[..^"/mcp".Length]}/authorize");

        var redirect = await FollowInBrowserAsync(start.AuthorizationUri!);
        Assert.AreEqual(RedirectUri, redirect.GetLeftPart(UriPartial.Path), "the browser comes back to the redirect URI the UI asked for");

        await harness.DeliverAsync(SignInTopic, CallbackFrom(redirect));
        var completed = await WaitForStatusAsync(harness, McpSignInState.Completed);
        Assert.AreEqual(start.SignInId, completed.SignInId);
        Assert.AreEqual(1, auth.CodeGrants);

        var response = await harness.InvokeAsync("echo", """{"text":"hi"}""");
        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("echo:hi", response.Content);

        // The tokens are on disk, and only there.
        var files = Directory.GetFiles(harness.OAuthTokenDirectory, "*.json");
        Assert.AreEqual(1, files.Length);
        if (!OperatingSystem.IsWindows())
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files[0]));
    }

    [TestMethod]
    public async Task SignedIn_SurvivesARestart_WithoutAskingAgain()
    {
        var (harness, auth) = await StartAsync();
        await using var _harness = harness;
        await SignInAsync(harness);

        await harness.RestartBridgeAsync();

        var response = await harness.InvokeAsync("echo", """{"text":"again"}""");
        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("echo:again", response.Content);
        Assert.IsFalse(Statuses(harness).Any(s => s.State == McpSignInState.Required));
        Assert.AreEqual(1, auth.CodeGrants, "no second sign-in");
    }

    [TestMethod]
    public async Task ExpiredAccessToken_IsRefreshed_Transparently()
    {
        var (harness, auth) = await StartAsync();
        await using var _harness = harness;
        await SignInAsync(harness);

        auth.ExpireAccessTokens();

        var response = await harness.InvokeAsync("echo", """{"text":"later"}""");
        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("echo:later", response.Content);
        Assert.IsTrue(auth.RefreshGrants >= 1, "the refresh token was used");
    }

    [TestMethod]
    public async Task RevokedGrant_TurnsIntoAuthRequired_AndAFreshSignInRecovers()
    {
        var (harness, auth) = await StartAsync();
        await using var _harness = harness;
        await SignInAsync(harness);

        auth.RevokeAll();
        var response = await harness.InvokeAsync("echo", """{"text":"x"}""");
        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "needs the user to sign in");

        // The signed-out connection is dropped, so later calls don't retry the failed refresh.
        var refreshesAfterFailure = auth.RefreshGrants;
        response = await harness.InvokeAsync("echo", """{"text":"again"}""");
        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "needs the user to sign in");
        var listed = (await harness.RequestAsync(SignInTopic, new McpSignInListRequest()))
            .GetPayload<McpSignInListResponse>()!.Servers.Single();
        Assert.IsFalse(listed.Connected);
        Assert.AreEqual(McpSignInState.Required, listed.State);
        Assert.AreEqual(refreshesAfterFailure, auth.RefreshGrants);
        Assert.AreEqual(2, Statuses(harness).Count(s => s.State == McpSignInState.Required),
            "the revoked grant is reported again, after the first sign-in cleared it");

        await SignInAsync(harness);
        response = await harness.InvokeAsync("echo", """{"text":"y"}""");
        Assert.IsFalse(response.IsError, response.Content);
    }

    [TestMethod]
    public async Task DynamicRegistration_HappensOnlyForTheSignInTheUserStarts()
    {
        // No client id: the bridge registers with the authorization server. Startup, restarts and
        // calls before the sign-in must not each register a client (servers rate-limit DCR).
        var (harness, auth) = await StartAsync(clientId: null);
        await using var _harness = harness;

        await WaitForStatusAsync(harness, McpSignInState.Required);
        await harness.RestartBridgeAsync();
        var refused = await harness.InvokeAsync("echo", """{"text":"x"}""");
        StringAssert.Contains(refused.Content, "needs the user to sign in");
        Assert.AreEqual(0, auth.Registrations);

        await SignInAsync(harness);
        Assert.AreEqual(1, auth.Registrations);

        var response = await harness.InvokeAsync("echo", """{"text":"dcr"}""");
        Assert.IsFalse(response.IsError, response.Content);

        // The registration is kept with the tokens: a restart refreshes without registering again.
        await harness.RestartBridgeAsync();
        auth.ExpireAccessTokens();
        response = await harness.InvokeAsync("echo", """{"text":"later"}""");
        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual(1, auth.Registrations);
        Assert.IsTrue(auth.RefreshGrants >= 1);
    }

    [TestMethod]
    public async Task RefusedConsent_FailsTheSignIn_WithTheReason()
    {
        var (harness, _) = await StartAsync();
        await using var _harness = harness;

        var start = await StartSignInAsync(harness);
        var state = HttpUtility.ParseQueryString(new Uri(start.AuthorizationUri!).Query)["state"]!;
        await harness.DeliverAsync(SignInTopic, new McpSignInCallback
        {
            State = state,
            Error = "access_denied",
            ErrorDescription = "The user said no",
        });

        var failed = await WaitForStatusAsync(harness, McpSignInState.Failed);
        Assert.AreEqual(start.SignInId, failed.SignInId);
        StringAssert.Contains(failed.Message, "access_denied");
        StringAssert.Contains(failed.Message, "The user said no");
    }

    [TestMethod]
    public async Task CallbackForNoSignIn_IsIgnored()
    {
        var (harness, _) = await StartAsync();
        await using var _harness = harness;

        await harness.DeliverAsync(SignInTopic, new McpSignInCallback { State = "forged", Code = "abc" });

        Assert.IsFalse(Statuses(harness).Any(s => McpSignInState.IsTerminal(s.State)));
    }

    [TestMethod]
    public async Task NewSignIn_ReplacesTheOneUnderWay()
    {
        var (harness, _) = await StartAsync();
        await using var _harness = harness;

        var first = await StartSignInAsync(harness);
        var second = await StartSignInAsync(harness);

        var cancelled = await WaitForStatusAsync(harness, McpSignInState.Cancelled);
        Assert.AreEqual(first.SignInId, cancelled.SignInId);

        // The first tab's redirect now goes nowhere; the second's completes.
        await harness.DeliverAsync(SignInTopic, CallbackFrom(await FollowInBrowserAsync(first.AuthorizationUri!)));
        await harness.DeliverAsync(SignInTopic, CallbackFrom(await FollowInBrowserAsync(second.AuthorizationUri!)));
        var completed = await WaitForStatusAsync(harness, McpSignInState.Completed);
        Assert.AreEqual(second.SignInId, completed.SignInId);
    }

    [TestMethod]
    public async Task StartSignIn_ForAServerWithoutOAuth_IsRefused()
    {
        await using var harness = await BridgeHarness.StartAsync([Echo()]);

        var unknown = await StartSignInAsync(harness, "nope");
        StringAssert.Contains(unknown.Error, "no MCP server named");

        var plain = await StartSignInAsync(harness);
        StringAssert.Contains(plain.Error, "doesn't use OAuth");
    }

    [TestMethod]
    public async Task List_ReportsWhereEachServerStands()
    {
        var (harness, _) = await StartAsync();
        await using var _harness = harness;

        async Task<McpSignInServer> ListOneAsync()
        {
            var reply = await harness.RequestAsync(SignInTopic, new McpSignInListRequest());
            return reply.GetPayload<McpSignInListResponse>()!.Servers.Single();
        }

        var before = await ListOneAsync();
        Assert.AreEqual(McpSignInState.Required, before.State);
        Assert.IsFalse(before.Connected);

        await SignInAsync(harness);

        var after = await ListOneAsync();
        Assert.AreEqual(McpSignInState.SignedIn, after.State);
        Assert.IsTrue(after.Connected);
    }
}
