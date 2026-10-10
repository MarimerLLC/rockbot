using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Wisp;

namespace RockBot.Wisp.Tests;

/// <summary>
/// #685: a wisp's direct step runs inside its parent's tool call, under the parent run's
/// consequential-action scope. Under a subagent spawned from an information-only user message, an
/// MCP write is refused without reaching the tool; reads and instructed writes run.
/// </summary>
[TestClass]
public class WispConsequentialActionGateTests
{
    private static readonly IConsequentialActionGate Gate =
        new ConsequentialActionGate(Options.Create(new AgentHostOptions()));

    [TestMethod]
    public async Task DirectMcpWrite_UnderInformationOnlySubagent_IsRefused()
    {
        var (executor, calls) = CreateExecutor();
        using var scope = ActionGateContext.Set(
            new ActionGateScope(RunOrigin.SubagentOfUserTurn, UserRequestKind.InformationOnly), Gate);

        var result = await executor.ExecuteAsync(Wisp("create_event"), "wisp-gate-1",
            parentSessionId: "subagent/abc123", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(0, calls.Count, "the calendar must not be touched");
        var step = result.StepResults.Single();
        StringAssert.StartsWith(step.Error!.Message, "Not run: create_event (via mcp_invoke_tool) would change calendar-mcp");
        Assert.AreEqual(FailureCategory.Judgment, step.Error.Category);
    }

    [TestMethod]
    public async Task DirectMcpRead_UnderInformationOnlySubagent_Runs()
    {
        var (executor, calls) = CreateExecutor();
        using var scope = ActionGateContext.Set(
            new ActionGateScope(RunOrigin.SubagentOfUserTurn, UserRequestKind.InformationOnly), Gate);

        var result = await executor.ExecuteAsync(Wisp("list_events"), "wisp-gate-2",
            parentSessionId: "subagent/abc123", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.StepResults[0].Error?.Message);
        Assert.AreEqual(1, calls.Count);
    }

    [TestMethod]
    public async Task DirectMcpWrite_UnderInstructedSubagent_Runs()
    {
        var (executor, calls) = CreateExecutor();
        using var scope = ActionGateContext.Set(
            new ActionGateScope(RunOrigin.SubagentOfUserTurn, UserRequestKind.Instruction), Gate);

        var result = await executor.ExecuteAsync(Wisp("create_event"), "wisp-gate-3",
            parentSessionId: "subagent/abc123", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.StepResults[0].Error?.Message);
        Assert.AreEqual(1, calls.Count);
    }

    private static WispDefinition Wisp(string tool) => new()
    {
        Description = "Calendar step",
        Steps =
        [
            new WispStep
            {
                Id = "step",
                Mode = StepMode.Direct,
                Gateway = GatewayType.Mcp,
                Server = "calendar-mcp",
                Tool = tool,
                Params = JsonDocument.Parse("""{"title":"Prep block"}""").RootElement
            }
        ]
    };

    private static (WispExecutor Executor, List<string> Calls) CreateExecutor()
    {
        var calls = new List<string>();
        var registry = new FakeToolRegistry();
        registry.Register(
            new ToolRegistration { Name = "mcp_invoke_tool", Description = "MCP", Source = "mcp:management" },
            new TrackingToolExecutor("ok", calls, "mcp_invoke_tool"));
        var executor = new WispExecutor(registry, new FakeWorkingMemory(), agentLoopRunner: null!,
            new WispOptions(), NullLogger<WispExecutor>.Instance);
        return (executor, calls);
    }
}
