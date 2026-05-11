using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.A2A;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Tools;
using RockBot.Messaging;
using RockBot.Messaging.InProcess;

namespace RockBot.AdvisorCouncil.Tests;

[TestClass]
public class ResearchAgentInvokerTests
{
    [TestMethod]
    public async Task InvokeAsync_PublishesRequest_AndReturnsResultText()
    {
        var sp = BuildServices(researchTimeoutSec: 5);
        var invoker = sp.GetRequiredService<ResearchAgentInvoker>();
        var publisher = sp.GetRequiredService<IMessagePublisher>();
        var subscriber = sp.GetRequiredService<IMessageSubscriber>();

        // Start the invoker so its reply queue subscription is active.
        await invoker.StartAsync(CancellationToken.None);

        // Simulate ResearchAgent: subscribe to its task topic, send a result back to replyTo
        // with the same correlationId.
        await using var fakeAgentSubscription = await subscriber.SubscribeAsync(
            "agent.task.ResearchAgent",
            "fake-research-agent",
            async (envelope, ct) =>
            {
                var reply = new AgentTaskResult
                {
                    TaskId = envelope.CorrelationId!,
                    State = AgentTaskState.Completed,
                    Message = new AgentMessage
                    {
                        Role = "agent",
                        Parts = [new AgentMessagePart { Kind = "text", Text = "Mocked research answer." }]
                    }
                };
                var replyEnvelope = reply.ToEnvelope<AgentTaskResult>(
                    source: "ResearchAgent",
                    correlationId: envelope.CorrelationId);
                await publisher.PublishAsync(envelope.ReplyTo!, replyEnvelope, ct);
                return MessageResult.Ack;
            });

        var result = await invoker.InvokeAsync("What is the capital of France?", CancellationToken.None);

        Assert.AreEqual("Mocked research answer.", result);

        await invoker.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task InvokeAsync_TimesOut_WhenNoReplyArrives()
    {
        var sp = BuildServices(researchTimeoutSec: 1);
        var invoker = sp.GetRequiredService<ResearchAgentInvoker>();
        await invoker.StartAsync(CancellationToken.None);

        // No subscriber on agent.task.ResearchAgent — nothing will reply.

        var result = await invoker.InvokeAsync("Anything", CancellationToken.None);

        StringAssert.Contains(result, "timed out");
        await invoker.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task InvokeAsync_AgentTaskError_SurfacesErrorMessage()
    {
        var sp = BuildServices(researchTimeoutSec: 5);
        var invoker = sp.GetRequiredService<ResearchAgentInvoker>();
        var publisher = sp.GetRequiredService<IMessagePublisher>();
        var subscriber = sp.GetRequiredService<IMessageSubscriber>();
        await invoker.StartAsync(CancellationToken.None);

        await using var fakeAgentSubscription = await subscriber.SubscribeAsync(
            "agent.task.ResearchAgent",
            "fake-research-error",
            async (envelope, ct) =>
            {
                var err = new AgentTaskError
                {
                    TaskId = envelope.CorrelationId!,
                    Code = AgentTaskError.Codes.ExecutionFailed,
                    Message = "Web search quota exceeded.",
                    IsRetryable = false
                };
                var replyEnvelope = err.ToEnvelope<AgentTaskError>(
                    source: "ResearchAgent",
                    correlationId: envelope.CorrelationId);
                await publisher.PublishAsync(envelope.ReplyTo!, replyEnvelope, ct);
                return MessageResult.Ack;
            });

        var result = await invoker.InvokeAsync("anything", CancellationToken.None);

        StringAssert.Contains(result, "Web search quota exceeded");
        await invoker.StopAsync(CancellationToken.None);
    }

    private static IServiceProvider BuildServices(int researchTimeoutSec)
    {
        var services = new ServiceCollection();
        services.AddRockBotInProcessMessaging();
        services.AddLogging();
        services.Configure<CouncilOptions>(o => o.ResearchAgentTimeoutSeconds = researchTimeoutSec);
        services.AddSingleton<ResearchAgentInvoker>();
        return services.BuildServiceProvider();
    }
}
