using System.Security.Claims;
using System.Text.Json;
using A2A;
using RockBot.A2A;
using RockBot.A2A.Gateway.Auth;
using RockBot.Messaging;

namespace RockBot.A2A.Gateway.Tests;

/// <summary>
/// Tests for the metadata and part-mapping helpers in <see cref="RockBotBridgeHandler"/>.
/// These helpers carry A2A v1 request/message metadata and non-text parts across
/// the HTTP → RabbitMQ bridge so capability handlers can read them.
/// </summary>
[TestClass]
public class RockBotBridgeHandlerTests
{
    [TestMethod]
    public void ExtractRequestMetadata_DropsSkillKey_AndStringifiesRest()
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            ["skill"] = JsonDocument.Parse("\"extract-structured-data\"").RootElement,
            ["tenant"] = JsonDocument.Parse("\"acme\"").RootElement,
            ["priority"] = JsonDocument.Parse("42").RootElement
        };

        var result = RockBotBridgeHandler.ExtractRequestMetadata(metadata);

        Assert.IsNotNull(result);
        Assert.IsFalse(result.ContainsKey("skill"), "skill key should be consumed for routing, not duplicated");
        Assert.AreEqual("acme", result["tenant"]);
        Assert.AreEqual("42", result["priority"]);
    }

    [TestMethod]
    public void ExtractRequestMetadata_NullOrEmpty_ReturnsNull()
    {
        Assert.IsNull(RockBotBridgeHandler.ExtractRequestMetadata(null));
        Assert.IsNull(RockBotBridgeHandler.ExtractRequestMetadata(new Dictionary<string, JsonElement>()));
    }

    [TestMethod]
    public void ExtractRequestMetadata_OnlySkillKey_ReturnsNull()
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            ["skill"] = JsonDocument.Parse("\"foo\"").RootElement
        };

        Assert.IsNull(RockBotBridgeHandler.ExtractRequestMetadata(metadata));
    }

    [TestMethod]
    public void StringifyMetadata_PreservesAllKeys()
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            ["url"] = JsonDocument.Parse("\"https://example.com\"").RootElement,
            ["count"] = JsonDocument.Parse("7").RootElement,
            ["enabled"] = JsonDocument.Parse("true").RootElement
        };

        var result = RockBotBridgeHandler.StringifyMetadata(metadata);

        Assert.IsNotNull(result);
        Assert.AreEqual("https://example.com", result["url"]);
        Assert.AreEqual("7", result["count"]);
        Assert.AreEqual("true", result["enabled"]);
    }

    [TestMethod]
    public void MapInboundParts_TextPart_MapsToTextKind()
    {
        var parts = new List<Part>
        {
            new() { Text = "hello" }
        };

        var mapped = RockBotBridgeHandler.MapInboundParts(parts, fallbackText: "(empty)");

        Assert.AreEqual(1, mapped.Count);
        Assert.AreEqual("text", mapped[0].Kind);
        Assert.AreEqual("hello", mapped[0].Text);
    }

    [TestMethod]
    public void MapInboundParts_DataPart_MapsToDataKindWithRawJson()
    {
        var dataJson = JsonDocument.Parse("{\"url\":\"https://example.com\"}").RootElement;
        var parts = new List<Part>
        {
            Part.FromData(dataJson)
        };
        parts[0].MediaType = "application/json";

        var mapped = RockBotBridgeHandler.MapInboundParts(parts, fallbackText: "(empty)");

        Assert.AreEqual(1, mapped.Count);
        Assert.AreEqual("data", mapped[0].Kind);
        Assert.AreEqual("application/json", mapped[0].MimeType);
        Assert.IsNotNull(mapped[0].Data);
        var parsed = JsonDocument.Parse(mapped[0].Data!).RootElement;
        Assert.AreEqual("https://example.com", parsed.GetProperty("url").GetString());
    }

    [TestMethod]
    public void MapInboundParts_MixedTextAndData_PreservesBoth()
    {
        var dataJson = JsonDocument.Parse("{\"k\":1}").RootElement;
        var parts = new List<Part>
        {
            new() { Text = "describe" },
            Part.FromData(dataJson)
        };

        var mapped = RockBotBridgeHandler.MapInboundParts(parts, fallbackText: "(empty)");

        Assert.AreEqual(2, mapped.Count);
        Assert.AreEqual("text", mapped[0].Kind);
        Assert.AreEqual("data", mapped[1].Kind);
    }

    [TestMethod]
    public void MapInboundParts_EmptyList_UsesFallbackText()
    {
        var mapped = RockBotBridgeHandler.MapInboundParts([], fallbackText: "(empty)");

        Assert.AreEqual(1, mapped.Count);
        Assert.AreEqual("text", mapped[0].Kind);
        Assert.AreEqual("(empty)", mapped[0].Text);
    }

    [TestMethod]
    public void MapOutboundParts_TextPart_MapsToA2ATextPart()
    {
        var parts = new List<AgentMessagePart>
        {
            new() { Kind = "text", Text = "reply" }
        };

        var mapped = RockBotBridgeHandler.MapOutboundParts(parts);

        Assert.AreEqual(1, mapped.Count);
        Assert.AreEqual(PartContentCase.Text, mapped[0].ContentCase);
        Assert.AreEqual("reply", mapped[0].Text);
    }

    [TestMethod]
    public void MapOutboundParts_DataPart_MapsToA2ADataPart()
    {
        var parts = new List<AgentMessagePart>
        {
            new() { Kind = "data", Data = "{\"ok\":true}", MimeType = "application/json" }
        };

        var mapped = RockBotBridgeHandler.MapOutboundParts(parts);

        Assert.AreEqual(1, mapped.Count);
        Assert.AreEqual(PartContentCase.Data, mapped[0].ContentCase);
        Assert.AreEqual("application/json", mapped[0].MediaType);
        Assert.IsTrue(mapped[0].Data!.Value.GetProperty("ok").GetBoolean());
    }

    [TestMethod]
    public void MapOutboundParts_NullOrEmpty_EmitsPlaceholder()
    {
        var mapped = RockBotBridgeHandler.MapOutboundParts(null);
        Assert.AreEqual(1, mapped.Count);
        Assert.AreEqual("(no response)", mapped[0].Text);

        mapped = RockBotBridgeHandler.MapOutboundParts([]);
        Assert.AreEqual(1, mapped.Count);
        Assert.AreEqual("(no response)", mapped[0].Text);
    }

    [TestMethod]
    public void ToJsonElementMetadata_StringValues_ProduceStringJsonElements()
    {
        var source = new Dictionary<string, string>
        {
            ["url"] = "https://example.com",
            ["description"] = "pick the title"
        };

        var result = RockBotBridgeHandler.ToJsonElementMetadata(source);

        Assert.IsNotNull(result);
        Assert.AreEqual(JsonValueKind.String, result["url"].ValueKind);
        Assert.AreEqual("https://example.com", result["url"].GetString());
        Assert.AreEqual("pick the title", result["description"].GetString());
    }

    [TestMethod]
    public void ToJsonElementMetadata_NullOrEmpty_ReturnsNull()
    {
        Assert.IsNull(RockBotBridgeHandler.ToJsonElementMetadata(null));
        Assert.IsNull(RockBotBridgeHandler.ToJsonElementMetadata(new Dictionary<string, string>()));
    }

    [TestMethod]
    public void BuildAuthClaimsHeader_BearerPrincipal_EmitsVerifiedClaims()
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "caller-123", null, "https://idp.example.com"),
                new Claim(ClaimTypes.Name, "Caller Agent"),
                new Claim("scope", "a2a.invoke")
            },
            authenticationType: "Bearer");
        var user = new ClaimsPrincipal(identity);

        var headers = RockBotBridgeHandler.BuildAuthClaimsHeader(user);

        Assert.IsNotNull(headers);
        Assert.IsTrue(headers.ContainsKey(WellKnownHeaders.AuthClaims));
        var claims = JsonSerializer.Deserialize<Dictionary<string, string>>(headers[WellKnownHeaders.AuthClaims])!;
        Assert.AreEqual("caller-123", claims["sub"]);
        Assert.AreEqual("Caller Agent", claims["name"]);
        Assert.AreEqual("a2a.invoke", claims["scope"]);
        // Falls back to the claim's Issuer property when no explicit "iss" claim is present.
        Assert.AreEqual("https://idp.example.com", claims["iss"]);
    }

    [TestMethod]
    public void BuildAuthClaimsHeader_ApiKeyPrincipal_ReturnsNull()
    {
        // Mirrors ApiKeyAuthenticationHandler's claims (issuer=api-key).
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "peer-agent"),
                new Claim(ClaimTypes.Name, "Peer Agent"),
                new Claim("issuer", "api-key")
            },
            authenticationType: ApiKeyAuthenticationHandler.SchemeName);
        var user = new ClaimsPrincipal(identity);

        Assert.IsNull(RockBotBridgeHandler.BuildAuthClaimsHeader(user));
    }

    [TestMethod]
    public void BuildAuthClaimsHeader_Unauthenticated_ReturnsNull()
    {
        Assert.IsNull(RockBotBridgeHandler.BuildAuthClaimsHeader(null));
        Assert.IsNull(RockBotBridgeHandler.BuildAuthClaimsHeader(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
