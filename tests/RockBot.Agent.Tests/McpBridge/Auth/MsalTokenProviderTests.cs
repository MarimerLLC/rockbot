using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using RockBot.Agent.McpBridge.Auth;
using RockBot.Tools.Mcp.Auth;

namespace RockBot.Agent.Tests.McpBridge.Auth;

[TestClass]
public class MsalTokenProviderTests
{
    [TestMethod]
    public async Task GetAccessTokenAsync_WithoutCachedAccount_ThrowsNotAuthenticated()
    {
        var msal = PublicClientApplicationBuilder
            .Create("00000000-0000-0000-0000-000000000001")
            .WithAuthority(AzureCloudInstance.AzurePublic, "00000000-0000-0000-0000-000000000002")
            .Build();

        var options = Options.Create(new MsalTokenProviderOptions
        {
            TenantId = "00000000-0000-0000-0000-000000000002",
            ClientId = "00000000-0000-0000-0000-000000000001",
            Scopes = ["api://test/.default"]
        });

        var publisher = new StubMessagePublisher();
        var provider = new MsalTokenProvider(
            msal, options, publisher, NullLogger<MsalTokenProvider>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(
            () => provider.GetAccessTokenAsync(forceRefresh: false, CancellationToken.None));

        Assert.AreEqual(TokenAcquisitionException.Codes.NotAuthenticated, ex.Code);
        StringAssert.Contains(ex.Message, "Connect M365");
        // No account, so no expired notification — the user simply hasn't consented yet.
        Assert.AreEqual(0, publisher.Published.Count);
    }

    [TestMethod]
    public async Task AddWorkIqAuth_WiresUpTokenProviderRegistry()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["WorkIQ:TenantId"] = "00000000-0000-0000-0000-000000000002",
            ["WorkIQ:ClientId"] = "00000000-0000-0000-0000-000000000001",
            ["WorkIQ:Scopes"] = "scope-a,scope-b"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<RockBot.Messaging.IMessagePublisher>(new StubMessagePublisher());
        services.AddLogging();
        services.AddWorkIqAuth(config);

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ITokenProviderRegistry>();
        var tokenProvider = registry.Get("workiq");
        Assert.IsNotNull(tokenProvider);
        Assert.IsInstanceOfType<MsalTokenProvider>(tokenProvider);

        var opts = provider.GetRequiredService<IOptions<MsalTokenProviderOptions>>().Value;
        CollectionAssert.AreEqual(new[] { "scope-a", "scope-b" }, opts.Scopes);
        Assert.IsTrue(opts.Enabled);
    }
}
