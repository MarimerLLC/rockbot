using System.Text.Json;
using ModelContextProtocol.Protocol;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

[TestClass]
public class McpToolExecutorTests
{
    [TestMethod]
    public async Task ExecuteAsync_CallsToolAndReturnsContent()
    {
        CallToolDelegate callTool = (_, _) => new ValueTask<CallToolResult>(new CallToolResult
        {
            Content = [new TextContentBlock { Text = "file contents here" }]
        });

        var executor = new McpToolExecutor(callTool);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "read_file",
            Arguments = """{"path": "/tmp/test.txt"}"""
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual("call_1", response.ToolCallId);
        Assert.AreEqual("read_file", response.ToolName);
        Assert.AreEqual("file contents here", response.Content);
        Assert.IsFalse(response.IsError);
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsIsError_WhenToolReportsError()
    {
        CallToolDelegate callTool = (_, _) => new ValueTask<CallToolResult>(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "something went wrong" }]
        });

        var executor = new McpToolExecutor(callTool);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "fail_tool"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        Assert.AreEqual("something went wrong", response.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_HandlesEmptyContent()
    {
        CallToolDelegate callTool = (_, _) => new ValueTask<CallToolResult>(new CallToolResult
        {
            Content = []
        });

        var executor = new McpToolExecutor(callTool);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "no_output"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsNull(response.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_HandlesNullArguments()
    {
        Dictionary<string, object?>? capturedArgs = null;
        CallToolDelegate callTool = (args, _) =>
        {
            capturedArgs = args?.ToDictionary(kv => kv.Key, kv => kv.Value);
            return new ValueTask<CallToolResult>(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "pong" }]
            });
        };

        var executor = new McpToolExecutor(callTool);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "ping",
            Arguments = null
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual("pong", response.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_ConcatenatesMultipleTextContent()
    {
        CallToolDelegate callTool = (_, _) => new ValueTask<CallToolResult>(new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = "line 1" },
                new TextContentBlock { Text = "line 2" }
            ]
        });

        var executor = new McpToolExecutor(callTool);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "multi_output"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual("line 1\nline 2", response.Content);
    }

    [TestMethod]
    public void ParseArguments_HandlesNullInput()
    {
        var result = McpToolExecutor.ParseArguments(null);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseArguments_HandlesEmptyString()
    {
        var result = McpToolExecutor.ParseArguments("");
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseArguments_ParsesJsonObject()
    {
        var result = McpToolExecutor.ParseArguments("""{"key": "value", "num": 42}""");
        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.ContainsKey("key"));
        Assert.IsTrue(result.ContainsKey("num"));
    }

    [TestMethod]
    public void FormatResult_ReturnsNull_ForEmptyContent()
    {
        var result = new CallToolResult { Content = [] };
        Assert.IsNull(McpToolExecutor.FormatResult(result));
    }

    [TestMethod]
    public void FormatResult_ExtractsTextContent()
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "hello" }]
        };
        Assert.AreEqual("hello", McpToolExecutor.FormatResult(result));
    }
}
