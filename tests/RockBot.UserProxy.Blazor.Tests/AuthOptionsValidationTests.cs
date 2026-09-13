using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RockBot.UserProxy.Blazor.Auth;

namespace RockBot.UserProxy.Blazor.Tests;

[TestClass]
public class AuthOptionsValidationTests
{
    private static AuthOptions Options(
        bool enabled,
        bool withProvider = true,
        string[]? emails = null,
        string[]? domains = null)
    {
        var options = new AuthOptions { Enabled = enabled };

        if (withProvider)
        {
            options.Providers["Google"] = new OAuthProviderOptions
            {
                ClientId = "client-id",
                ClientSecret = "client-secret",
            };
        }

        foreach (var email in emails ?? []) options.AllowedEmails.Add(email);
        foreach (var domain in domains ?? []) options.AllowedDomains.Add(domain);

        return options;
    }

    [TestMethod]
    public void Disabled_WithNothingConfigured_IsValid()
    {
        // The default shape of every existing deployment. It must stay valid.
        CollectionAssert.AreEqual(Array.Empty<string>(), new AuthOptions().Validate().ToArray());
    }

    [TestMethod]
    public void Disabled_WithAPartialConfiguration_IsStillValid()
    {
        // Half-written config that has not been switched on yet is not an error.
        Assert.AreEqual(0, Options(enabled: false, withProvider: false).Validate().Count());
    }

    [TestMethod]
    public void Enabled_WithNoProvider_IsRejected()
    {
        var problems = Options(enabled: true, withProvider: false, domains: ["example.com"]).Validate().ToList();

        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0], "no identity provider is configured");
    }

    [TestMethod]
    public void Enabled_WithAHalfConfiguredProvider_IsRejected()
    {
        var options = Options(enabled: true, withProvider: false, domains: ["example.com"]);
        options.Providers["Google"] = new OAuthProviderOptions { ClientId = "client-id" };  // no secret

        Assert.IsTrue(options.Validate().Any(p => p.Contains("no identity provider is configured")));
    }

    [TestMethod]
    public void Enabled_WithAnEmptyAllowlist_IsRejected()
    {
        var problems = Options(enabled: true).Validate().ToList();

        Assert.AreEqual(1, problems.Count);
        // The message has to say why, because "it wouldn't start" is otherwise indistinguishable
        // from a bug — and the failure it prevents is an internet-facing open door.
        StringAssert.Contains(problems[0], "every account at");
    }

    [TestMethod]
    public void Enabled_WithABlankAllowlistEntry_IsRejected()
    {
        Assert.IsTrue(Options(enabled: true, emails: ["  "], domains: [""]).Validate().Any());
    }

    [TestMethod]
    public void Enabled_WithAProviderAndAnAllowlist_IsValid()
    {
        Assert.AreEqual(0, Options(enabled: true, domains: ["example.com"]).Validate().Count());
        Assert.AreEqual(0, Options(enabled: true, emails: ["someone@example.com"]).Validate().Count());
    }

    [TestMethod]
    public void Enabled_WithAnUnsupportedProviderName_IsRejected()
    {
        var options = Options(enabled: true, domains: ["example.com"]);
        options.Providers["Twitter"] = new OAuthProviderOptions { ClientId = "a", ClientSecret = "b" };

        Assert.IsTrue(options.Validate().Any(p => p.Contains("Twitter")));
    }

    [TestMethod]
    public void AddRockBotAuth_ThrowsOnAnInvalidConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Enabled"] = "true",
                ["Auth:Providers:Google:ClientId"] = "client-id",
                ["Auth:Providers:Google:ClientSecret"] = "client-secret",
                // No allowlist.
            })
            .Build();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => new ServiceCollection().AddRockBotAuth(configuration));

        StringAssert.Contains(ex.Message, "AllowedDomains");
    }

    [TestMethod]
    public void AddRockBotAuth_BindsAndRegistersAWorkingConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Enabled"] = "true",
                ["Auth:SessionLifetime"] = "7.00:00:00",
                ["Auth:Providers:Google:ClientId"] = "client-id",
                ["Auth:Providers:Google:ClientSecret"] = "client-secret",
                ["Auth:AllowedDomains:0"] = "example.com",
            })
            .Build();

        var options = new ServiceCollection().AddRockBotAuth(configuration);

        Assert.IsTrue(options.Enabled);
        Assert.AreEqual(TimeSpan.FromDays(7), options.SessionLifetime);
        CollectionAssert.AreEqual(new[] { "example.com" }, options.AllowedDomains.ToArray());
    }

    [TestMethod]
    public void SessionLifetime_DefaultsToFourteenDays()
    {
        Assert.AreEqual(TimeSpan.FromDays(14), new AuthOptions().SessionLifetime);
    }
}
