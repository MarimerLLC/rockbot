using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using RockBot.Agent.McpBridge.Auth;
using RockBot.Messaging;
using RockBot.UserProxy.WorkIqAuth;

namespace RockBot.Agent.Tests.McpBridge.Auth;

[TestClass]
public class TokenCacheStoreTests
{
    private string _cacheDir = null!;
    private string _cachePath = null!;

    [TestInitialize]
    public void Init()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "rockbot-tokencache-tests-" + Guid.NewGuid().ToString("N"));
        _cachePath = Path.Combine(_cacheDir, "workiq-cache.bin");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
    }

    [TestMethod]
    public async Task HandleCacheUpdated_PersistsBytesToDisk()
    {
        var ctx = await StartStoreAsync();

        var payload = new WorkIqAuthCacheUpdated
        {
            CacheBytes = [1, 2, 3, 4, 5],
            AccountId = "user@example.com",
            Scopes = ["WorkIQ-Mail.Read"]
        };
        var envelope = payload.ToEnvelope(source: "ui");

        var result = await ctx.Subscriber.Handler!.Invoke(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result);
        Assert.IsTrue(File.Exists(_cachePath));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(_cachePath));

        await ctx.Store.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task HandleCacheUpdated_EmptyPayload_DeadLetters()
    {
        var ctx = await StartStoreAsync();

        var payload = new WorkIqAuthCacheUpdated
        {
            CacheBytes = [],
            AccountId = "user"
        };
        var envelope = payload.ToEnvelope(source: "ui");

        var result = await ctx.Subscriber.Handler!.Invoke(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.DeadLetter, result);
        Assert.IsFalse(File.Exists(_cachePath));

        await ctx.Store.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Start_CreatesCacheDirectory()
    {
        Assert.IsFalse(Directory.Exists(_cacheDir));

        var ctx = await StartStoreAsync();

        Assert.IsTrue(Directory.Exists(_cacheDir));

        await ctx.Store.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Start_SubscribesToCorrectTopic()
    {
        var ctx = await StartStoreAsync();

        Assert.AreEqual(WorkIqAuthTopics.CacheUpdated, ctx.Subscriber.Topic);

        await ctx.Store.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task PersistedFile_HasUserRwPermissions_OnLinux()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("Unix file mode is only meaningful on POSIX systems.");
            return;
        }

        var ctx = await StartStoreAsync();

        var payload = new WorkIqAuthCacheUpdated
        {
            CacheBytes = [42],
            AccountId = "user"
        };
        await ctx.Subscriber.Handler!.Invoke(payload.ToEnvelope(source: "ui"), CancellationToken.None);

        var mode = File.GetUnixFileMode(_cachePath);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);

        await ctx.Store.StopAsync(CancellationToken.None);
    }

    private async Task<StoreContext> StartStoreAsync()
    {
        var subscriber = new StubMessageSubscriber();
        // PublicClientApplicationBuilder does not hit the network until you ask
        // it to acquire a token, so this is safe in unit tests.
        var msal = PublicClientApplicationBuilder
            .Create("00000000-0000-0000-0000-000000000001")
            .WithAuthority(AzureCloudInstance.AzurePublic, "00000000-0000-0000-0000-000000000002")
            .Build();

        var options = Options.Create(new MsalTokenProviderOptions
        {
            CacheFilePath = _cachePath,
            TenantId = "00000000-0000-0000-0000-000000000002",
            ClientId = "00000000-0000-0000-0000-000000000001"
        });

        var store = new TokenCacheStore(
            subscriber, msal, options, NullLogger<TokenCacheStore>.Instance);
        await store.StartAsync(CancellationToken.None);
        return new StoreContext(store, subscriber, msal);
    }

    private sealed record StoreContext(
        TokenCacheStore Store, StubMessageSubscriber Subscriber, IPublicClientApplication Msal);
}
