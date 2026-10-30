using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;
using RockBot.Agent.McpBridge.Auth;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>The bridge's durable OAuth tokens for MCP servers (#738).</summary>
[TestClass]
public class McpOAuthTokenStoreTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup() =>
        _dir = Path.Combine(Path.GetTempPath(), "rockbot-oauth-store-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private McpOAuthTokenStore NewStore() => new(_dir, NullLogger.Instance);

    private static TokenContainer Tokens(string access = "at", string refresh = "rt") => new()
    {
        TokenType = "Bearer",
        AccessToken = access,
        RefreshToken = refresh,
        ExpiresIn = 3600,
        ObtainedAt = DateTimeOffset.UtcNow,
        ClientId = "registered-client",
        AuthorizationServer = "https://as.example",
    };

    [TestMethod]
    public async Task StoredTokens_SurviveANewStore_WithTheClientRegistration()
    {
        await NewStore().CacheFor("https://srv/mcp", "c").StoreTokensAsync(Tokens(), CancellationToken.None);

        var read = await NewStore().CacheFor("https://srv/mcp", "c").GetTokensAsync(CancellationToken.None);

        Assert.AreEqual("at", read!.AccessToken);
        Assert.AreEqual("rt", read.RefreshToken);
        Assert.AreEqual("registered-client", read.ClientId);
        Assert.AreEqual("https://as.example", read.AuthorizationServer);

        var file = Directory.GetFiles(_dir).Single();
        StringAssert.Contains(File.ReadAllText(file), "\"version\": 1");
        if (!OperatingSystem.IsWindows())
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [TestMethod]
    public void Key_FollowsTheServerAndClient_NotTheSpelling()
    {
        Assert.AreEqual(
            McpOAuthTokenStore.KeyFor("https://SRV/mcp/", "c"),
            McpOAuthTokenStore.KeyFor("https://srv/mcp", " c "));
        Assert.AreNotEqual(McpOAuthTokenStore.KeyFor("https://srv/mcp", "c"), McpOAuthTokenStore.KeyFor("https://srv/mcp", "d"));
        Assert.AreNotEqual(McpOAuthTokenStore.KeyFor("https://srv/mcp", "c"), McpOAuthTokenStore.KeyFor("https://other/mcp", "c"));
        Assert.IsFalse(McpOAuthTokenStore.KeyFor("https://srv/mcp", "c").Contains("srv"), "the file name doesn't reveal the server");
    }

    [TestMethod]
    public async Task FreshCache_HidesTheOldSignIn_UntilItStoresItsOwn()
    {
        var store = NewStore();
        await store.CacheFor("https://srv/mcp", null).StoreTokensAsync(Tokens("old"), CancellationToken.None);

        var fresh = store.FreshCacheFor("https://srv/mcp", null);
        Assert.IsNull(await fresh.GetTokensAsync(CancellationToken.None));

        await fresh.StoreTokensAsync(Tokens("new"), CancellationToken.None);
        Assert.AreEqual("new", (await fresh.GetTokensAsync(CancellationToken.None))!.AccessToken);
        Assert.AreEqual("new", (await store.CacheFor("https://srv/mcp", null).GetTokensAsync(CancellationToken.None))!.AccessToken,
            "what a sign-in stores replaces the old tokens for every connection");
    }

    [TestMethod]
    public async Task UnreadableFile_IsSignedOut_AndKeptAside()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, McpOAuthTokenStore.KeyFor("https://srv/mcp", null) + ".json");
        File.WriteAllText(path, "{ not json");

        var store = NewStore();
        Assert.IsNull(await store.CacheFor("https://srv/mcp", null).GetTokensAsync(CancellationToken.None));
        Assert.IsFalse(await store.HasTokensAsync("https://srv/mcp", null));
        Assert.IsTrue(File.Exists(path + ".corrupt"));
    }

    [TestMethod]
    public async Task ConcurrentReads_AllSeeTheTokens()
    {
        await NewStore().CacheFor("https://srv/mcp", null).StoreTokensAsync(Tokens(), CancellationToken.None);
        var cache = NewStore().CacheFor("https://srv/mcp", null);

        var reads = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () => await cache.GetTokensAsync(CancellationToken.None))));

        Assert.IsTrue(reads.All(t => t?.AccessToken == "at"));
    }
}
