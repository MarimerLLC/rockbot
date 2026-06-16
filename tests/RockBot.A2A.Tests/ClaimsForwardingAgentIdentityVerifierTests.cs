using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Messaging;

namespace RockBot.A2A.Tests;

[TestClass]
public class ClaimsForwardingAgentIdentityVerifierTests
{
    private static ClaimsForwardingAgentIdentityVerifier CreateVerifier() =>
        new(new NameBasedAgentIdentityVerifier(),
            NullLogger<ClaimsForwardingAgentIdentityVerifier>.Instance);

    private static MessageEnvelope EnvelopeWithHeaders(
        string source, IReadOnlyDictionary<string, string>? headers)
    {
        var payload = new AgentTaskRequest
        {
            TaskId = "t1",
            Skill = "test",
            Message = new AgentMessage { Role = "user", Parts = [] }
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        return MessageEnvelope.Create(
            messageType: typeof(AgentTaskRequest).FullName!,
            body: body,
            source: source,
            headers: headers);
    }

    [TestMethod]
    public async Task ForwardedClaims_ProduceVerifiedNonSelfAssertedIdentity()
    {
        var claims = new Dictionary<string, string>
        {
            ["sub"] = "caller-123",
            ["name"] = "Caller Agent",
            ["iss"] = "https://idp.example.com",
            ["scope"] = "a2a.invoke"
        };
        var envelope = EnvelopeWithHeaders(
            source: "gateway-relayed",
            headers: new Dictionary<string, string>
            {
                [WellKnownHeaders.AuthClaims] = JsonSerializer.Serialize(claims)
            });

        var identity = await CreateVerifier().VerifyAsync(envelope, CancellationToken.None);

        Assert.IsFalse(identity.IsSelfAsserted);
        Assert.AreEqual("caller-123", identity.AgentId);
        Assert.AreEqual("Caller Agent", identity.DisplayName);
        Assert.AreEqual("https://idp.example.com", identity.Issuer);
        Assert.IsNotNull(identity.Claims);
        Assert.AreEqual("a2a.invoke", identity.Claims!["scope"]);
    }

    [TestMethod]
    public async Task NoClaimsHeader_FallsBackToNameBased()
    {
        var envelope = EnvelopeWithHeaders(source: "TestAgent", headers: null);

        var identity = await CreateVerifier().VerifyAsync(envelope, CancellationToken.None);

        Assert.IsTrue(identity.IsSelfAsserted);
        Assert.AreEqual("TestAgent", identity.AgentId);
        Assert.AreEqual("self", identity.Issuer);
    }

    [TestMethod]
    public async Task EmptySource_NoClaims_ThrowsViaFallback()
    {
        var envelope = new MessageEnvelope
        {
            MessageId = "test",
            MessageType = typeof(AgentTaskRequest).FullName!,
            Body = ReadOnlyMemory<byte>.Empty,
            Source = "",
            Timestamp = DateTimeOffset.UtcNow
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CreateVerifier().VerifyAsync(envelope, CancellationToken.None));
    }

    [TestMethod]
    public async Task ClaimsWithoutSub_FallsBackToSourceAsAgentId()
    {
        var claims = new Dictionary<string, string> { ["name"] = "No Subject" };
        var envelope = EnvelopeWithHeaders(
            source: "source-fallback",
            headers: new Dictionary<string, string>
            {
                [WellKnownHeaders.AuthClaims] = JsonSerializer.Serialize(claims)
            });

        var identity = await CreateVerifier().VerifyAsync(envelope, CancellationToken.None);

        Assert.IsFalse(identity.IsSelfAsserted);
        Assert.AreEqual("source-fallback", identity.AgentId);
        Assert.AreEqual("No Subject", identity.DisplayName);
    }

    [TestMethod]
    public async Task MalformedClaimsJson_Throws()
    {
        var envelope = EnvelopeWithHeaders(
            source: "x",
            headers: new Dictionary<string, string>
            {
                [WellKnownHeaders.AuthClaims] = "{not-json"
            });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CreateVerifier().VerifyAsync(envelope, CancellationToken.None));
    }
}
