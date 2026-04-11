using RockBot.Messaging;

namespace RockBot.A2A.Tests;

[TestClass]
public class IdentityVerificationTests
{
    [TestMethod]
    public async Task NameBasedVerifier_ReturnsIdentityFromSource()
    {
        var verifier = new NameBasedAgentIdentityVerifier();
        var envelope = TestEnvelopeHelper.CreateEnvelope(
            new AgentTaskRequest
            {
                TaskId = "t1",
                Skill = "test",
                Message = new AgentMessage { Role = "user", Parts = [] }
            },
            source: "TestAgent");

        var identity = await verifier.VerifyAsync(envelope, CancellationToken.None);

        Assert.AreEqual("TestAgent", identity.AgentId);
        Assert.AreEqual("TestAgent", identity.DisplayName);
        Assert.AreEqual("self", identity.Issuer);
        Assert.IsTrue(identity.IsSelfAsserted);
    }

    [TestMethod]
    public async Task NameBasedVerifier_ThrowsWhenSourceEmpty()
    {
        var verifier = new NameBasedAgentIdentityVerifier();
        var envelope = TestEnvelopeHelper.CreateEnvelope(
            new AgentTaskRequest
            {
                TaskId = "t1",
                Skill = "test",
                Message = new AgentMessage { Role = "user", Parts = [] }
            },
            source: "");

        // Source is required so we need to construct an envelope with empty source manually
        var emptySourceEnvelope = new MessageEnvelope
        {
            MessageId = "test",
            MessageType = typeof(AgentTaskRequest).FullName!,
            Body = envelope.Body,
            Source = "",
            Timestamp = DateTimeOffset.UtcNow
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => verifier.VerifyAsync(emptySourceEnvelope, CancellationToken.None));
    }

    [TestMethod]
    public void VerifiedAgentIdentity_ContextKey_IsCorrect()
    {
        Assert.AreEqual("verified-identity", VerifiedAgentIdentity.ContextKey);
    }
}
