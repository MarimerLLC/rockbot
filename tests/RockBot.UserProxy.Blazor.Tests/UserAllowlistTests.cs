using System.Security.Claims;
using RockBot.UserProxy.Blazor.Auth;

namespace RockBot.UserProxy.Blazor.Tests;

[TestClass]
public class UserAllowlistTests
{
    private static UserAllowlist Allowlist(string[]? emails = null, string[]? domains = null) =>
        new(emails ?? [], domains ?? []);

    private static ClaimsPrincipal SignedIn(string? email, string? emailVerified = null)
    {
        var claims = new List<Claim>();
        if (email is not null)
            claims.Add(new Claim(ClaimTypes.Email, email));
        if (emailVerified is not null)
            claims.Add(new Claim("email_verified", emailVerified));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestScheme"));
    }

    [TestMethod]
    public void ExactEmail_Matches()
    {
        Assert.IsTrue(Allowlist(emails: ["someone@example.com"]).IsAllowed("someone@example.com"));
    }

    [TestMethod]
    public void ExactEmail_IsCaseInsensitiveOnBothParts()
    {
        var allowlist = Allowlist(emails: ["SomeOne@Example.COM"]);

        Assert.IsTrue(allowlist.IsAllowed("someone@example.com"));
        Assert.IsTrue(allowlist.IsAllowed("SOMEONE@EXAMPLE.COM"));
    }

    [TestMethod]
    public void UnlistedEmail_IsDenied()
    {
        Assert.IsFalse(Allowlist(emails: ["someone@example.com"]).IsAllowed("someone-else@example.com"));
    }

    [TestMethod]
    public void Domain_MatchesAnyAddressInIt()
    {
        var allowlist = Allowlist(domains: ["example.com"]);

        Assert.IsTrue(allowlist.IsAllowed("anyone@example.com"));
        Assert.IsTrue(allowlist.IsAllowed("ANYONE@EXAMPLE.COM"));
    }

    [TestMethod]
    public void Domain_IsMatchedExactly_NotAsASuffix()
    {
        var allowlist = Allowlist(domains: ["example.com"]);

        // The whole point of matching the domain exactly: an attacker registers a domain that ends
        // with the allowed one and would otherwise walk straight in.
        Assert.IsFalse(allowlist.IsAllowed("attacker@evil-example.com"));
        Assert.IsFalse(allowlist.IsAllowed("attacker@example.com.evil.test"));
    }

    [TestMethod]
    public void Domain_MatchesThePartAfterTheFinalAt()
    {
        var allowlist = Allowlist(domains: ["example.com"]);

        Assert.IsFalse(allowlist.IsAllowed("attacker@example.com@evil.test"));
        Assert.IsTrue(allowlist.IsAllowed("odd@name@example.com"));
    }

    [TestMethod]
    public void Domain_LeadingAtIsTolerated()
    {
        Assert.IsTrue(Allowlist(domains: ["@example.com"]).IsAllowed("anyone@example.com"));
    }

    [TestMethod]
    public void EmptyAllowlist_DeniesEveryone()
    {
        var allowlist = Allowlist();

        Assert.IsTrue(allowlist.IsEmpty);
        Assert.IsFalse(allowlist.IsAllowed("anyone@example.com"));
        Assert.IsFalse(allowlist.IsAllowed(SignedIn("anyone@example.com")));
    }

    [TestMethod]
    public void MalformedAddress_IsDenied()
    {
        var allowlist = Allowlist(domains: ["example.com"]);

        Assert.IsFalse(allowlist.IsAllowed("no-at-sign"));
        Assert.IsFalse(allowlist.IsAllowed("@example.com"));
        Assert.IsFalse(allowlist.IsAllowed("trailing@"));
        Assert.IsFalse(allowlist.IsAllowed(""));
        Assert.IsFalse(allowlist.IsAllowed((string?)null));
    }

    [TestMethod]
    public void AnonymousPrincipal_IsDenied()
    {
        var allowlist = Allowlist(domains: ["example.com"]);

        // No authentication type means IsAuthenticated is false, even with an email claim present.
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "anyone@example.com")]));

        Assert.IsFalse(allowlist.IsAllowed(anonymous));
        Assert.IsFalse(allowlist.IsAllowed((ClaimsPrincipal?)null));
    }

    [TestMethod]
    public void PrincipalWithNoEmailClaim_IsDenied()
    {
        Assert.IsFalse(Allowlist(domains: ["example.com"]).IsAllowed(SignedIn(email: null)));
    }

    [TestMethod]
    public void UnverifiedEmail_IsDenied()
    {
        var allowlist = Allowlist(emails: ["someone@example.com"], domains: ["example.com"]);

        // An unverified address proves nothing about who controls it, so it must not satisfy an
        // exact-match rule either — anyone can type someone else's address at a sloppy provider.
        Assert.IsFalse(allowlist.IsAllowed(SignedIn("someone@example.com", emailVerified: "false")));
    }

    [TestMethod]
    public void VerifiedEmail_IsAllowed()
    {
        Assert.IsTrue(Allowlist(domains: ["example.com"]).IsAllowed(SignedIn("someone@example.com", "true")));
    }

    [TestMethod]
    public void AbsentVerificationClaim_IsNotTreatedAsUnverified()
    {
        // Absent means "this provider does not publish the claim", which is not evidence of a
        // problem. Only a literal false is.
        Assert.IsTrue(Allowlist(domains: ["example.com"]).IsAllowed(SignedIn("someone@example.com")));
    }

    [TestMethod]
    public void ShortOidcEmailClaim_IsRead()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("email", "someone@example.com")], authenticationType: "TestScheme"));

        Assert.AreEqual("someone@example.com", UserAllowlist.GetEmail(principal));
        Assert.IsTrue(Allowlist(domains: ["example.com"]).IsAllowed(principal));
    }

    [TestMethod]
    public void BlankConfiguredEntries_AreIgnored()
    {
        var allowlist = Allowlist(emails: ["", "   "], domains: ["", "   "]);

        Assert.IsTrue(allowlist.IsEmpty);
    }
}
