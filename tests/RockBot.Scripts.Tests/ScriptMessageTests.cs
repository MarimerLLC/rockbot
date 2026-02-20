using RockBot.Scripts;

namespace RockBot.Scripts.Tests;

[TestClass]
public class ScriptMessageTests
{
    [TestMethod]
    public void ScriptInvokeRequest_DefaultTimeout_Is30()
    {
        var request = new ScriptInvokeRequest
        {
            ToolCallId = "call_1",
            Script = "print('hello')"
        };

        Assert.AreEqual(30, request.TimeoutSeconds);
    }

    [TestMethod]
    public void ScriptInvokeRequest_PipPackages_DefaultsToNull()
    {
        var request = new ScriptInvokeRequest
        {
            ToolCallId = "call_1",
            Script = "print('hello')"
        };

        Assert.IsNull(request.PipPackages);
    }

    [TestMethod]
    public void ScriptInvokeResponse_IsSuccess_TrueWhenExitCodeZero()
    {
        var response = new ScriptInvokeResponse
        {
            ToolCallId = "call_1",
            Output = "hello",
            ExitCode = 0,
            ElapsedMs = 100
        };

        Assert.IsTrue(response.IsSuccess);
    }

    [TestMethod]
    public void ScriptInvokeResponse_IsSuccess_FalseWhenExitCodeNonZero()
    {
        var response = new ScriptInvokeResponse
        {
            ToolCallId = "call_1",
            Stderr = "error",
            ExitCode = 1,
            ElapsedMs = 100
        };

        Assert.IsFalse(response.IsSuccess);
    }
}
