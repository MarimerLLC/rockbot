using RockBot.Tools.Mcp.Auth;

namespace RockBot.Tools.Tests.Auth;

[TestClass]
public class TokenProviderRegistryTests
{
    [TestMethod]
    public void Get_RegisteredProfile_ReturnsProvider()
    {
        var provider = new StubProvider("token-A");
        var registry = new TokenProviderRegistry(
            [new TokenProviderRegistration("workiq", provider)]);

        Assert.AreSame(provider, registry.Get("workiq"));
    }

    [TestMethod]
    public void Get_ProfileMatchIsCaseInsensitive()
    {
        var provider = new StubProvider("token-A");
        var registry = new TokenProviderRegistry(
            [new TokenProviderRegistration("workiq", provider)]);

        Assert.AreSame(provider, registry.Get("WorkIQ"));
        Assert.AreSame(provider, registry.Get("WORKIQ"));
    }

    [TestMethod]
    public void Get_UnknownProfile_ThrowsWithKnownProfilesListed()
    {
        var registry = new TokenProviderRegistry(
        [
            new TokenProviderRegistration("workiq", new StubProvider("a")),
            new TokenProviderRegistration("github", new StubProvider("b"))
        ]);

        var ex = Assert.ThrowsExactly<KeyNotFoundException>(
            () => registry.Get("nonexistent"));

        StringAssert.Contains(ex.Message, "nonexistent");
        StringAssert.Contains(ex.Message, "workiq");
        StringAssert.Contains(ex.Message, "github");
    }

    private sealed class StubProvider(string token) : ITokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct) =>
            Task.FromResult(token);
    }
}
