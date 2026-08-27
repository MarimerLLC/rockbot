using RockBot.UserProxy.Blazor.Auth;

namespace RockBot.UserProxy.Blazor.Tests;

[TestClass]
public class AuthProviderRegistryTests
{
    private static AuthOptions Enabled(bool clientId = true, bool clientSecret = true)
    {
        var options = new AuthOptions { Enabled = true };
        options.Providers["Google"] = new OAuthProviderOptions
        {
            ClientId = clientId ? "client-id" : "",
            ClientSecret = clientSecret ? "client-secret" : "",
        };
        options.AllowedDomains.Add("example.com");
        return options;
    }

    [TestMethod]
    public void ConfiguredProvider_IsAdvertised()
    {
        var registry = new AuthProviderRegistry(Enabled());

        Assert.AreEqual(1, registry.Enabled.Count);
        Assert.AreEqual("Google", registry.Enabled[0].Key);
        Assert.AreEqual(AuthProviderRegistry.GoogleScheme, registry.Enabled[0].Scheme);
    }

    [TestMethod]
    public void HalfConfiguredProvider_IsNotAdvertised()
    {
        // A provider missing half its credential would fail at the redirect with an opaque provider
        // error. Better never to offer the button.
        Assert.AreEqual(0, new AuthProviderRegistry(Enabled(clientSecret: false)).Enabled.Count);
        Assert.AreEqual(0, new AuthProviderRegistry(Enabled(clientId: false)).Enabled.Count);
    }

    [TestMethod]
    public void AuthDisabled_AdvertisesNothing()
    {
        var options = Enabled();
        options.Enabled = false;

        Assert.AreEqual(0, new AuthProviderRegistry(options).Enabled.Count);
    }

    [TestMethod]
    public void Resolve_MatchesCaseInsensitively()
    {
        var registry = new AuthProviderRegistry(Enabled());

        Assert.IsNotNull(registry.Resolve("google"));
        Assert.IsNotNull(registry.Resolve("GOOGLE"));
    }

    [TestMethod]
    public void Resolve_RejectsUnknownOrUnconfiguredProviders()
    {
        var registry = new AuthProviderRegistry(Enabled());

        // This is what keeps /auth/challenge from challenging a scheme that was never registered.
        Assert.IsNull(registry.Resolve("Twitter"));
        Assert.IsNull(registry.Resolve(""));
        Assert.IsNull(registry.Resolve(null));
        Assert.IsNull(new AuthProviderRegistry(Enabled(clientSecret: false)).Resolve("Google"));
    }

    [TestMethod]
    public void KnownProviders_CoversWhatTheBuildSupports()
    {
        Assert.IsTrue(AuthProviderRegistry.IsKnownProvider("google"));
        Assert.IsFalse(AuthProviderRegistry.IsKnownProvider("Twitter"));
        CollectionAssert.Contains(AuthProviderRegistry.KnownProviders.ToArray(), "Google");
    }
}

[TestClass]
public class LocalReturnUrlTests
{
    [TestMethod]
    public void LocalPaths_AreAccepted()
    {
        Assert.IsTrue(LocalReturnUrl.IsLocal("/"));
        Assert.IsTrue(LocalReturnUrl.IsLocal("/chat"));
        Assert.IsTrue(LocalReturnUrl.IsLocal("/chat?tab=saved#top"));
    }

    [TestMethod]
    public void AbsoluteUrls_AreRejected()
    {
        Assert.IsFalse(LocalReturnUrl.IsLocal("https://evil.test/"));
        Assert.IsFalse(LocalReturnUrl.IsLocal("http://evil.test/"));
        Assert.IsFalse(LocalReturnUrl.IsLocal("javascript:alert(1)"));
    }

    [TestMethod]
    public void ProtocolRelativeForms_AreRejected()
    {
        // Both slash characters: browsers normalise a backslash to a forward slash in authority
        // position, so "/\evil.test" navigates off-site just as "//evil.test" does.
        Assert.IsFalse(LocalReturnUrl.IsLocal("//evil.test/"));
        Assert.IsFalse(LocalReturnUrl.IsLocal("/\\evil.test/"));
        Assert.IsFalse(LocalReturnUrl.IsLocal("/:evil"));
    }

    [TestMethod]
    public void RelativeAndEmptyForms_AreRejected()
    {
        Assert.IsFalse(LocalReturnUrl.IsLocal("chat"));
        Assert.IsFalse(LocalReturnUrl.IsLocal(""));
        Assert.IsFalse(LocalReturnUrl.IsLocal(null));
    }

    [TestMethod]
    public void ControlCharacters_AreRejected()
    {
        // A newline in a value that ends up in a Location header is response splitting.
        Assert.IsFalse(LocalReturnUrl.IsLocal("/chat\r\nSet-Cookie: x=y"));
    }

    [TestMethod]
    public void Sanitize_FallsBackToRoot()
    {
        Assert.AreEqual("/", LocalReturnUrl.Sanitize("https://evil.test/"));
        Assert.AreEqual("/", LocalReturnUrl.Sanitize(null));
        Assert.AreEqual("/chat", LocalReturnUrl.Sanitize("/chat"));
    }
}
