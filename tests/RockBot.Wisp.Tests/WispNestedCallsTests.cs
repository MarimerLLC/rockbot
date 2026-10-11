using RockBot.Host;

namespace RockBot.Wisp.Tests;

/// <summary>
/// The tool calls a wisp's steps make (#686), which are reported on the parent's <c>spawn_wisps</c>
/// call. They are named by the downstream tool. An LLM step whose writes all failed has failed.
/// </summary>
[TestClass]
public class WispNestedCallsTests
{
    [TestMethod]
    public void NestedCall_McpStep_IsNamedByServerAndTool()
    {
        var step = new WispStep { Id = "create", Mode = StepMode.Direct, Gateway = GatewayType.Mcp, Server = "calendar-mcp", Tool = "create_event" };

        var call = WispExecutor.NestedCall(step, "wisp-9eaa", "mcp_invoke_tool", """{"server_name":"calendar-mcp"}""", succeeded: false);

        Assert.AreEqual("calendar-mcp__create_event", call.Name);
        Assert.IsFalse(call.Succeeded);
        Assert.AreEqual("wisp wisp-9eaa step create", call.Detail);
        Assert.IsTrue(ToolSideEffects.IsSideEffecting(call));
    }

    [TestMethod]
    public void NestedCall_OtherTool_KeepsItsName_AndShortensArguments()
    {
        var step = new WispStep { Id = "save", Mode = StepMode.Direct, Tool = "file_write" };

        var call = WispExecutor.NestedCall(step, "wisp-1", "file_write", new string('x', 1000), succeeded: true);

        Assert.AreEqual("file_write", call.Name);
        Assert.AreEqual(300, call.Arguments!.Length);
    }

    [TestMethod]
    public void AllStateChangesFailed_EveryWriteFailed_IsAnError()
    {
        var error = WispExecutor.AllStateChangesFailed(
        [
            new LoopToolCall("calendar-mcp__list_calendars", null, true),
            new LoopToolCall("calendar-mcp__create_event", null, false),
            new LoopToolCall("calendar-mcp__create_event", null, false),
        ]);

        Assert.IsNotNull(error);
        Assert.AreEqual(FailureCategory.External, error.Category);
        StringAssert.Contains(error.Message, "All 2 state-changing tool call(s) of this step failed (calendar-mcp__create_event)");
    }

    [TestMethod]
    public void AllStateChangesFailed_OneWriteSucceeded_OrNoWrites_IsNotAnError()
    {
        Assert.IsNull(WispExecutor.AllStateChangesFailed(null));
        Assert.IsNull(WispExecutor.AllStateChangesFailed([new LoopToolCall("calendar-mcp__get_event", null, false)]),
            "a failed read is the step's own business");
        Assert.IsNull(WispExecutor.AllStateChangesFailed(
        [
            new LoopToolCall("calendar-mcp__create_event", null, false),
            new LoopToolCall("calendar-mcp__create_event", null, true),
        ]));
    }

    [TestMethod]
    public void ExecutionResult_ToolCalls_AreTheStepsCallsInOrder()
    {
        var result = new WispExecutionResult
        {
            WispId = "wisp-1",
            IsSuccess = true,
            Duration = TimeSpan.Zero,
            Definition = new WispDefinition { Description = "d", Steps = [] },
            StepResults =
            [
                new WispStepResult { StepId = "a", StepIndex = 0, IsSuccess = true, Duration = TimeSpan.Zero,
                    ToolCalls = [new LoopToolCall("one", null, true)] },
                new WispStepResult { StepId = "b", StepIndex = 1, IsSuccess = true, Duration = TimeSpan.Zero, WasSkipped = true },
                new WispStepResult { StepId = "c", StepIndex = 2, IsSuccess = true, Duration = TimeSpan.Zero,
                    ToolCalls = [new LoopToolCall("two", null, true), new LoopToolCall("three", null, false)] },
            ],
        };

        CollectionAssert.AreEqual(new[] { "one", "two", "three" }, result.ToolCalls.Select(c => c.Name).ToArray());
    }
}
