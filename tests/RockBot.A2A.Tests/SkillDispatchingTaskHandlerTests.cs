using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A.Tests;

[TestClass]
public class SkillDispatchingTaskHandlerTests
{
    private static MessageHandlerContext CreateMessageContext() => new()
    {
        Envelope = TestEnvelopeHelper.CreateEnvelope(
            new AgentTaskRequest
            {
                TaskId = "probe",
                Skill = "probe",
                Message = new AgentMessage
                {
                    Role = "user",
                    Parts = [new AgentMessagePart { Kind = "text", Text = "x" }]
                }
            }),
        Agent = new AgentIdentity("test-agent"),
        Services = null!,
        CancellationToken = CancellationToken.None
    };

    private static AgentTaskContext CreateTaskContext() => new()
    {
        MessageContext = CreateMessageContext(),
        PublishStatus = (_, _) => Task.CompletedTask
    };

    private static AgentTaskRequest Request(string skill) => new()
    {
        TaskId = "t1",
        Skill = skill,
        Message = new AgentMessage
        {
            Role = "user",
            Parts = [new AgentMessagePart { Kind = "text", Text = "hi" }]
        }
    };

    [TestMethod]
    public async Task RoutesRequest_ToMatchingSkillHandler()
    {
        var echo = new StubSkillHandler("echo", "Echo");
        var search = new StubSkillHandler("search", "Search");
        var dispatcher = new SkillDispatchingTaskHandler(
            [echo, search],
            NullLogger<SkillDispatchingTaskHandler>.Instance);

        await dispatcher.HandleTaskAsync(Request("search"), CreateTaskContext());

        Assert.AreEqual(1, search.InvocationCount);
        Assert.AreEqual(0, echo.InvocationCount);
    }

    [TestMethod]
    public async Task SkillIdMatch_IsCaseInsensitive()
    {
        var echo = new StubSkillHandler("echo", "Echo");
        var dispatcher = new SkillDispatchingTaskHandler(
            [echo],
            NullLogger<SkillDispatchingTaskHandler>.Instance);

        await dispatcher.HandleTaskAsync(Request("ECHO"), CreateTaskContext());

        Assert.AreEqual(1, echo.InvocationCount);
    }

    [TestMethod]
    public async Task UnknownSkill_ReturnsFailedResult_WithClearMessage()
    {
        var echo = new StubSkillHandler("echo", "Echo");
        var dispatcher = new SkillDispatchingTaskHandler(
            [echo],
            NullLogger<SkillDispatchingTaskHandler>.Instance);

        var result = await dispatcher.HandleTaskAsync(Request("unknown-skill"), CreateTaskContext());

        Assert.AreEqual(AgentTaskState.Failed, result.State);
        Assert.AreEqual(0, echo.InvocationCount);
        var text = result.Message?.Parts[0].Text ?? string.Empty;
        Assert.IsTrue(text.Contains("unknown-skill"), $"Expected error to mention skill id, got: {text}");
    }

    [TestMethod]
    public void DuplicateSkillId_ThrowsAtConstruction()
    {
        var a = new StubSkillHandler("echo", "Echo A");
        var b = new StubSkillHandler("echo", "Echo B");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            _ = new SkillDispatchingTaskHandler([a, b], NullLogger<SkillDispatchingTaskHandler>.Instance));
    }

    [TestMethod]
    public async Task SingleSkill_RoutesCorrectly()
    {
        var only = new StubSkillHandler("only", "Only");
        var dispatcher = new SkillDispatchingTaskHandler(
            [only],
            NullLogger<SkillDispatchingTaskHandler>.Instance);

        await dispatcher.HandleTaskAsync(Request("only"), CreateTaskContext());

        Assert.AreEqual(1, only.InvocationCount);
    }

    private sealed class StubSkillHandler(string id, string name) : IAgentSkillHandler
    {
        public AgentSkill Skill { get; } = new() { Id = id, Name = name };
        public int InvocationCount { get; private set; }

        public Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context)
        {
            InvocationCount++;
            return Task.FromResult(new AgentTaskResult
            {
                TaskId = request.TaskId,
                State = AgentTaskState.Completed,
                Message = new AgentMessage
                {
                    Role = "agent",
                    Parts = [new AgentMessagePart { Kind = "text", Text = $"{id}-done" }]
                }
            });
        }
    }
}
