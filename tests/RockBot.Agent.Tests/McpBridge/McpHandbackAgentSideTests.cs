using Microsoft.Extensions.AI;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// The agent's half of MCP hand-back (#602): <c>mcp_answer</c> follows the index, only a run that
/// offers it marks its calls as able to answer, and an answer travels the real executor, bus and
/// bridge back to the parked call.
/// </summary>
[TestClass]
public class McpHandbackAgentSideTests
{
    private const string Session = "session/test";

    private static Task<BridgeHarness> StartAsync(string mode = McpElicitationConfig.ModeHandback) =>
        BridgeHarness.StartAsync(
            [McpHandbackEndToEndTests.MailboxTool()],
            configure: e => e.Elicitation = new McpElicitationConfig { Mode = mode });

    /// <summary>A run's tool list that offers <c>mcp_answer</c>, as the primary agent's does.</summary>
    private static ChatOptions RunWithAnswerTool() => new()
    {
        Tools = [AIFunctionFactory.Create(() => "", McpHandbackContext.AnswerToolName)],
    };

    private static Task<ToolInvokeResponse> CallAsync(McpWrapperEndToEndTests.AgentSide agent, string tool, string arguments) =>
        agent.Registry.GetExecutor(tool)!.ExecuteAsync(
            new ToolInvokeRequest
            {
                ToolCallId = Guid.NewGuid().ToString("N"),
                ToolName = tool,
                Arguments = arguments,
                SessionId = Session,
            },
            CancellationToken.None);

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task McpAnswer_IsOfferedOnlyWhileAServerHandsBack()
    {
        await using (var handback = await StartAsync())
        {
            var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(handback, McpWrapperMode.Off);
            Assert.IsTrue(handback.IndexMessages.Last().Servers.Single().Handback);
            Assert.IsNotNull(agent.Registry.GetExecutor(McpHandbackContext.AnswerToolName));
        }

        await using (var auto = await StartAsync(McpElicitationConfig.ModeAuto))
        {
            var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(auto, McpWrapperMode.Off);
            Assert.IsFalse(auto.IndexMessages.Last().Servers.Single().Handback);
            Assert.IsNull(agent.Registry.GetExecutor(McpHandbackContext.AnswerToolName),
                "every other run's tool list stays as it was");
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ARunWithMcpAnswer_GetsTheQuestion_AndAnswersIt()
    {
        await using var harness = await StartAsync();
        var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(harness, McpWrapperMode.Off);

        using (McpHandbackContext.Set(RunWithAnswerTool()))
        {
            var handedBack = await CallAsync(agent, "mcp_invoke_tool",
                """{"server_name":"fixture","tool_name":"search_mail","arguments":{}}""");
            var id = McpHandbackEndToEndTests.IdIn(handedBack.Content);
            Assert.IsFalse(handedBack.IsError, "a hand-back is a result, and recovery leaves it alone");

            var answered = await CallAsync(agent, McpHandbackContext.AnswerToolName,
                $$$"""{"question_id":"{{{id}}}","answers":{"mailbox":"work"}}""");

            Assert.IsFalse(answered.IsError, answered.Content);
            StringAssert.StartsWith(answered.Content, "round 1 accept:work");
            Assert.AreEqual(McpHandbackContext.AnswerToolName, answered.ToolName);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ATypedWrapperCall_IsHandedBackToo()
    {
        await using var harness = await StartAsync();
        var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(harness, McpWrapperMode.Eager);

        using (McpHandbackContext.Set(RunWithAnswerTool()))
        {
            var handedBack = await CallAsync(agent, "fixture__search_mail", "{}");
            var id = McpHandbackEndToEndTests.IdIn(handedBack.Content);

            var declined = await CallAsync(agent, McpHandbackContext.AnswerToolName, $$"""{"question_id":"{{id}}","decline":true}""");

            StringAssert.StartsWith(declined.Content, "round 1 decline:");
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ARunWithoutMcpAnswer_OrAWispInsideOne_IsNeverHandedAQuestion()
    {
        await using var harness = await StartAsync();
        var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(harness, McpWrapperMode.Off);
        const string call = """{"server_name":"fixture","tool_name":"search_mail","arguments":{}}""";

        // No run marker at all: declined in-band (this server has no responder).
        StringAssert.StartsWith((await CallAsync(agent, "mcp_invoke_tool", call)).Content, "round 1 decline:");

        // A run whose tools don't include mcp_answer.
        using (McpHandbackContext.Set(new ChatOptions { Tools = [AIFunctionFactory.Create(() => "", "echo")] }))
            StringAssert.StartsWith((await CallAsync(agent, "mcp_invoke_tool", call)).Content, "round 1 decline:");

        // A wisp's step inside a run that could answer.
        using (McpHandbackContext.Set(RunWithAnswerTool()))
        using (McpHandbackContext.Suppress())
            StringAssert.StartsWith((await CallAsync(agent, "mcp_invoke_tool", call)).Content, "round 1 decline:");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task McpAnswer_ReportsARefusalAsAToolError()
    {
        await using var harness = await StartAsync();
        var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(harness, McpWrapperMode.Off);

        var missing = await CallAsync(agent, McpHandbackContext.AnswerToolName, """{"answers":{"mailbox":"work"}}""");
        var unknown = await CallAsync(agent, McpHandbackContext.AnswerToolName,
            """{"question_id":"q_00000000000000000000000000000000","answers":{"mailbox":"work"}}""");

        Assert.IsTrue(missing.IsError);
        StringAssert.Contains(missing.Content, "question_id");
        Assert.IsTrue(unknown.IsError);
        StringAssert.Contains(unknown.Content, "no open question");
    }

    [TestMethod]
    public void TheRunMarker_IgnoresACallerScopedMcpAnswer_AndRestores()
    {
        Assert.IsFalse(McpHandbackContext.CanAnswer);
        using (McpHandbackContext.Set(RunWithAnswerTool()))
        {
            Assert.IsTrue(McpHandbackContext.CanAnswer);
            using (McpHandbackContext.Suppress())
                Assert.IsFalse(McpHandbackContext.CanAnswer);
            Assert.IsTrue(McpHandbackContext.CanAnswer);
        }
        Assert.IsFalse(McpHandbackContext.CanAnswer);

        using (McpHandbackContext.Set(new ChatOptions { Tools = [new CallerScopedAnswer()] }))
            Assert.IsFalse(McpHandbackContext.CanAnswer, "a wisp's own tools never make it a run that can answer");
    }

    [TestMethod]
    public void SubagentsWorkersAndPatrols_DontGetMcpAnswer()
    {
        var registration = new ToolRegistration
        {
            Name = McpHandbackContext.AnswerToolName,
            Description = "",
            Source = "mcp:management",
        };

        Assert.IsTrue(ToolProfiles.Main.Matches(registration));
        Assert.IsTrue(ToolProfiles.A2ASynthesis.Matches(registration));
        Assert.IsFalse(ToolProfiles.Subagent.Matches(registration));
        Assert.IsFalse(ToolProfiles.Scheduled.Matches(registration));
    }

    private sealed class CallerScopedAnswer : AIFunction, ICallerScopedTool
    {
        public override string Name => McpHandbackContext.AnswerToolName;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(null);

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : base.GetService(serviceType, serviceKey);
    }
}
