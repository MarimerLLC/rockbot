using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A.Tests;

[TestClass]
public class IdentityVerificationMiddlewareTests
{
    [TestMethod]
    public async Task A2AMessage_StoresVerifiedIdentity()
    {
        var verifier = new NameBasedAgentIdentityVerifier();
        var middleware = new IdentityVerificationMiddleware(
            verifier, NullLogger<IdentityVerificationMiddleware>.Instance);

        var envelope = TestEnvelopeHelper.CreateEnvelope(
            new AgentTaskRequest
            {
                TaskId = "t1",
                Skill = "test",
                Message = new AgentMessage { Role = "user", Parts = [] }
            },
            source: "CallerAgent");

        var context = new MessageHandlerContext
        {
            Envelope = envelope,
            Agent = new AgentIdentity("RockBot"),
            Services = null!,
            CancellationToken = CancellationToken.None
        };

        var nextCalled = false;
        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.IsTrue(nextCalled);
        Assert.IsTrue(context.Items.ContainsKey(VerifiedAgentIdentity.ContextKey));
        var identity = (VerifiedAgentIdentity)context.Items[VerifiedAgentIdentity.ContextKey];
        Assert.AreEqual("CallerAgent", identity.AgentId);
        Assert.IsTrue(identity.IsSelfAsserted);
    }

    [TestMethod]
    public async Task NonA2AMessage_PassesThroughWithoutVerification()
    {
        var verifier = new NameBasedAgentIdentityVerifier();
        var middleware = new IdentityVerificationMiddleware(
            verifier, NullLogger<IdentityVerificationMiddleware>.Instance);

        // UserMessage is not an A2A message type
        var envelope = new MessageEnvelope
        {
            MessageId = "m1",
            MessageType = "RockBot.UserProxy.UserMessage",
            Body = new byte[] { },
            Source = "user",
            Timestamp = DateTimeOffset.UtcNow
        };

        var context = new MessageHandlerContext
        {
            Envelope = envelope,
            Agent = new AgentIdentity("RockBot"),
            Services = null!,
            CancellationToken = CancellationToken.None
        };

        var nextCalled = false;
        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.IsTrue(nextCalled);
        Assert.IsFalse(context.Items.ContainsKey(VerifiedAgentIdentity.ContextKey));
    }

    [TestMethod]
    public async Task VerificationFailure_DeadLettersMessage()
    {
        var verifier = new FailingVerifier();
        var middleware = new IdentityVerificationMiddleware(
            verifier, NullLogger<IdentityVerificationMiddleware>.Instance);

        var envelope = TestEnvelopeHelper.CreateEnvelope(
            new AgentTaskRequest
            {
                TaskId = "t1",
                Skill = "test",
                Message = new AgentMessage { Role = "user", Parts = [] }
            },
            source: "BadAgent");

        var context = new MessageHandlerContext
        {
            Envelope = envelope,
            Agent = new AgentIdentity("RockBot"),
            Services = null!,
            CancellationToken = CancellationToken.None
        };

        var nextCalled = false;
        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.IsFalse(nextCalled, "Next should not be called when verification fails");
        Assert.AreEqual(MessageResult.DeadLetter, context.Result);
    }

    private sealed class FailingVerifier : IAgentIdentityVerifier
    {
        public Task<VerifiedAgentIdentity> VerifyAsync(MessageEnvelope envelope, CancellationToken ct)
            => throw new InvalidOperationException("Verification failed");
    }
}
