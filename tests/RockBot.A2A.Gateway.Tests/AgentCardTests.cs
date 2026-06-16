using RockBot.A2A.Gateway.Auth;

namespace RockBot.A2A.Gateway.Tests;

/// <summary>
/// Tests for the agent-card security advertisement built by
/// <see cref="EndpointRouteBuilderExtensions.BuildAgentCard"/>.
/// </summary>
[TestClass]
public class AgentCardTests
{
    private static GatewayOptions SampleGateway() => new()
    {
        AgentName = "RockBot",
        Description = "Test agent",
        Version = "1.0",
        Skills = [new GatewaySkillConfig { Id = "notify", Name = "Notify" }]
    };

    [TestMethod]
    public void BuildAgentCard_JwtDisabled_AdvertisesApiKeyOnly()
    {
        var card = EndpointRouteBuilderExtensions.BuildAgentCard(SampleGateway(), new JwtAuthOptions());

        var schemes = card.SecuritySchemes!;
        CollectionAssert.AreEquivalent(new[] { "apiKey" }, schemes.Keys.ToList());
        var apiKey = schemes["apiKey"].ApiKeySecurityScheme!;
        Assert.AreEqual(ApiKeyAuthenticationHandler.HeaderName, apiKey.Name);

        var requirements = card.SecurityRequirements!;
        Assert.AreEqual(1, requirements.Count);
        Assert.IsTrue(requirements[0].Schemes!.ContainsKey("apiKey"));
    }

    [TestMethod]
    public void BuildAgentCard_JwtEnabled_AdvertisesBothSchemes()
    {
        var jwt = new JwtAuthOptions { Authority = "https://login.example.com/", Audience = "api://rockbot" };

        var card = EndpointRouteBuilderExtensions.BuildAgentCard(SampleGateway(), jwt);

        var schemes = card.SecuritySchemes!;
        CollectionAssert.AreEquivalent(
            new[] { "apiKey", "bearer", "openId" }, schemes.Keys.ToList());

        var bearer = schemes["bearer"].HttpAuthSecurityScheme!;
        Assert.AreEqual("bearer", bearer.Scheme);
        Assert.AreEqual("JWT", bearer.BearerFormat);

        var openId = schemes["openId"].OpenIdConnectSecurityScheme!;
        Assert.AreEqual("https://login.example.com/.well-known/openid-configuration",
            openId.OpenIdConnectUrl);

        // apiKey OR bearer — two independent requirement entries.
        var requirements = card.SecurityRequirements!;
        Assert.AreEqual(2, requirements.Count);
        Assert.IsTrue(requirements.Any(r => r.Schemes!.ContainsKey("apiKey")));
        Assert.IsTrue(requirements.Any(r => r.Schemes!.ContainsKey("bearer")));
        // The two schemes are in *separate* requirements (OR), not combined (AND).
        Assert.IsFalse(requirements.Any(r => r.Schemes!.Count > 1));
    }
}
